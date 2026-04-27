using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Transports;
using Piston.Roslyn;
using Piston.Roslyn.Messages;

namespace Piston.Roslyn.Worker;

internal sealed class WorkspaceHost
{
    private readonly StdioDuplexStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;

    private WorkspaceHost(StdioDuplexStream stream)
    {
        _stream = stream;
    }

    public static async Task RunAsync(Stream input, Stream output, CancellationToken ct)
    {
        var stream = new StdioDuplexStream(input, output);
        var host = new WorkspaceHost(stream);
        try
        {
            await host.ProcessMessagesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            host._workspace?.Dispose();
            host._writeLock.Dispose();
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessMessagesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var data = await MessageFramer.ReadMessageAsync(_stream, ct).ConfigureAwait(false);
            if (data is null)
            {
                break;
            }

            var message = JsonRpcSerializer.DeserializeMessage(data.Value);
            if (message is not JsonRpcRequest request)
            {
                continue;
            }

            JsonRpcResponse response;
            try
            {
                var result = request.Method switch
                {
                    RoslynMethods.LoadWorkspace => await HandleLoadWorkspace(request.Params, ct).ConfigureAwait(false),
                    RoslynMethods.GetDiagnostics => await HandleGetDiagnostics(request.Params, ct).ConfigureAwait(false),
                    RoslynMethods.SemanticSearch => await HandleSemanticSearch(request.Params, ct).ConfigureAwait(false),
                    RoslynMethods.GetAst => await HandleGetAst(request.Params, ct).ConfigureAwait(false),
                    RoslynMethods.Shutdown => HandleShutdown(),
                    _ => throw new NotSupportedException($"Unknown method: {request.Method}"),
                };

                response = new JsonRpcResponse(request.Id, result);
            }
            catch (Exception ex)
            {
                response = new JsonRpcResponse(
                    request.Id,
                    Error: new JsonRpcError(JsonRpcErrorCodes.InternalError, ex.Message));
            }

            var bytes = JsonRpcSerializer.Serialize(response);
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await MessageFramer.WriteMessageAsync(_stream, bytes, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            if (request.Method == RoslynMethods.Shutdown)
            {
                break;
            }
        }
    }

    private async Task<JsonNode?> HandleLoadWorkspace(JsonNode? @params, CancellationToken ct)
    {
        var loadParams = JsonSerializer.Deserialize(
            @params, RoslynJsonContext.Default.LoadWorkspaceParams)
            ?? throw new ArgumentException("Missing load workspace parameters.");

        var path = loadParams.Path;

        _workspace?.Dispose();
        _workspace = MSBuildWorkspace.Create();

        if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            _solution = await _workspace.OpenSolutionAsync(path, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        else if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var project = await _workspace.OpenProjectAsync(path, cancellationToken: ct)
                .ConfigureAwait(false);
            _solution = project.Solution;
        }
        else
        {
            throw new ArgumentException($"Unsupported file type: {path}. Expected .sln or .csproj.");
        }

        var projects = _solution.Projects.Select(p => new Messages.ProjectInfo(
            p.Name,
            p.FilePath ?? string.Empty,
            p.Documents.Count())).ToList();

        var info = new WorkspaceInfo(path, projects);
        return JsonSerializer.SerializeToNode(info, RoslynJsonContext.Default.WorkspaceInfo);
    }

    private async Task<JsonNode?> HandleGetDiagnostics(JsonNode? @params, CancellationToken ct)
    {
        EnsureSolutionLoaded();

        var diagParams = JsonSerializer.Deserialize(
            @params, RoslynJsonContext.Default.GetDiagnosticsParams)
            ?? throw new ArgumentException("Missing diagnostics parameters.");

        var results = new List<DiagnosticResult>();
        var projects = _solution!.Projects;

        if (diagParams.ProjectName is not null)
        {
            projects = projects.Where(p =>
                string.Equals(p.Name, diagParams.ProjectName, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var diagnostic in compilation.GetDiagnostics(ct))
            {
                if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
                {
                    continue;
                }

                var lineSpan = diagnostic.Location.GetLineSpan();
                results.Add(new DiagnosticResult(
                    diagnostic.Id,
                    diagnostic.GetMessage(),
                    diagnostic.Severity.ToString(),
                    lineSpan.Path ?? string.Empty,
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character + 1));
            }
        }

        var response = new DiagnosticsResponse(results);
        return JsonSerializer.SerializeToNode(response, RoslynJsonContext.Default.DiagnosticsResponse);
    }

    private async Task<JsonNode?> HandleSemanticSearch(JsonNode? @params, CancellationToken ct)
    {
        EnsureSolutionLoaded();

        var searchParams = JsonSerializer.Deserialize(
            @params, RoslynJsonContext.Default.SemanticSearchParams)
            ?? throw new ArgumentException("Missing semantic search parameters.");

        var symbolName = searchParams.SymbolName;
        var references = new List<SymbolReference>();
        string symbolKind = string.Empty;

        foreach (var project in _solution!.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            var symbols = compilation.GetSymbolsWithName(symbolName, SymbolFilter.All, ct);

            foreach (var symbol in symbols)
            {
                if (symbolKind.Length == 0)
                {
                    symbolKind = symbol.Kind.ToString();
                }

                var referencedSymbols = await SymbolFinder.FindReferencesAsync(
                    symbol, _solution, ct).ConfigureAwait(false);

                foreach (var referencedSymbol in referencedSymbols)
                {
                    foreach (var location in referencedSymbol.Locations)
                    {
                        var lineSpan = location.Location.GetLineSpan();
                        var containingMember = await GetContainingMemberNameAsync(
                            location.Document, lineSpan.StartLinePosition.Line,
                            lineSpan.StartLinePosition.Character, ct).ConfigureAwait(false);

                        references.Add(new SymbolReference(
                            lineSpan.Path ?? string.Empty,
                            lineSpan.StartLinePosition.Line + 1,
                            lineSpan.StartLinePosition.Character + 1,
                            containingMember));
                    }
                }
            }
        }

        var response = new SemanticSearchResponse(symbolName, symbolKind, references);
        return JsonSerializer.SerializeToNode(response, RoslynJsonContext.Default.SemanticSearchResponse);
    }

    private async Task<JsonNode?> HandleGetAst(JsonNode? @params, CancellationToken ct)
    {
        EnsureSolutionLoaded();

        var astParams = JsonSerializer.Deserialize(
            @params, RoslynJsonContext.Default.GetAstParams)
            ?? throw new ArgumentException("Missing AST parameters.");

        var filePath = astParams.FilePath;
        var maxDepth = astParams.MaxDepth;

        var document = _solution!.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Document not found: {filePath}");

        var syntaxTree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No syntax tree for: {filePath}");

        var root = await syntaxTree.GetRootAsync(ct).ConfigureAwait(false);
        var astRoot = BuildAstNode(root, 0, maxDepth);

        var response = new AstResponse(filePath, astRoot);
        return JsonSerializer.SerializeToNode(response, RoslynJsonContext.Default.AstResponse);
    }

    private static JsonNode? HandleShutdown()
    {
        return JsonSerializer.SerializeToNode(true, RoslynJsonContext.Default.Boolean);
    }

    private void EnsureSolutionLoaded()
    {
        if (_solution is null)
        {
            throw new InvalidOperationException("No workspace loaded. Call roslyn/loadWorkspace first.");
        }
    }

    private static async Task<string> GetContainingMemberNameAsync(
        Document document, int line, int character, CancellationToken ct)
    {
        var syntaxRoot = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (syntaxRoot is null)
        {
            return string.Empty;
        }

        var sourceText = await document.GetTextAsync(ct).ConfigureAwait(false);
        var position = sourceText.Lines[line].Start + character;
        var node = syntaxRoot.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0));

        while (node is not null)
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    return method.Identifier.Text;
                case PropertyDeclarationSyntax property:
                    return property.Identifier.Text;
                case ConstructorDeclarationSyntax constructor:
                    return constructor.Identifier.Text;
                case TypeDeclarationSyntax type:
                    return type.Identifier.Text;
            }

            node = node.Parent;
        }

        return string.Empty;
    }

    private static AstNode BuildAstNode(SyntaxNode node, int currentDepth, int maxDepth)
    {
        var name = GetNodeName(node);
        var returnType = GetReturnType(node);
        var modifiers = GetModifiers(node);
        var lineSpan = node.GetLocation().GetLineSpan();

        List<AstNode>? children = null;

        if (currentDepth < maxDepth)
        {
            var childDeclarations = new List<AstNode>();
            foreach (var child in node.ChildNodes())
            {
                if (IsDeclarationNode(child))
                {
                    childDeclarations.Add(BuildAstNode(child, currentDepth + 1, maxDepth));
                }
                else
                {
                    // Recurse into non-declaration nodes to find nested declarations
                    foreach (var nested in child.DescendantNodes())
                    {
                        if (IsDeclarationNode(nested) && nested.Parent == child)
                        {
                            childDeclarations.Add(BuildAstNode(nested, currentDepth + 1, maxDepth));
                        }
                    }
                }
            }

            if (childDeclarations.Count > 0)
            {
                children = childDeclarations;
            }
        }

        return new AstNode(
            node.GetType().Name.Replace("Syntax", string.Empty),
            name,
            returnType,
            modifiers,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.EndLinePosition.Line + 1,
            children);
    }

    private static bool IsDeclarationNode(SyntaxNode node)
    {
        return node is BaseNamespaceDeclarationSyntax
            or ClassDeclarationSyntax
            or RecordDeclarationSyntax
            or StructDeclarationSyntax
            or InterfaceDeclarationSyntax
            or EnumDeclarationSyntax
            or MethodDeclarationSyntax
            or PropertyDeclarationSyntax
            or FieldDeclarationSyntax
            or ConstructorDeclarationSyntax
            or EventDeclarationSyntax
            or DelegateDeclarationSyntax;
    }

    private static string? GetNodeName(SyntaxNode node)
    {
        return node switch
        {
            BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
            TypeDeclarationSyntax type => type.Identifier.Text,
            MethodDeclarationSyntax method => method.Identifier.Text,
            PropertyDeclarationSyntax property => property.Identifier.Text,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
            EventDeclarationSyntax @event => @event.Identifier.Text,
            DelegateDeclarationSyntax @delegate => @delegate.Identifier.Text,
            FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
            EnumDeclarationSyntax @enum => @enum.Identifier.Text,
            _ => null,
        };
    }

    private static string? GetReturnType(SyntaxNode node)
    {
        return node switch
        {
            MethodDeclarationSyntax method => method.ReturnType.ToString(),
            PropertyDeclarationSyntax property => property.Type.ToString(),
            DelegateDeclarationSyntax @delegate => @delegate.ReturnType.ToString(),
            FieldDeclarationSyntax field => field.Declaration.Type.ToString(),
            _ => null,
        };
    }

    private static string? GetModifiers(SyntaxNode node)
    {
        var tokenList = node switch
        {
            MemberDeclarationSyntax member => member.Modifiers,
            _ => default,
        };

        if (tokenList.Count == 0)
        {
            return null;
        }

        return string.Join(" ", tokenList.Select(m => m.Text));
    }
}
