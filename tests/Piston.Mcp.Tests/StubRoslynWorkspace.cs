using Piston.Roslyn;
using Piston.Roslyn.Messages;

namespace Piston.Mcp.Tests;

internal sealed class StubRoslynWorkspace : IRoslynWorkspace
{
    public bool IsLoaded { get; private set; }
    public string? LastLoadedPath { get; private set; }
    public string? LastFileChangedPath { get; private set; }

    public Task<WorkspaceInfo> LoadAsync(string solutionPath, CancellationToken ct)
    {
        IsLoaded = true;
        LastLoadedPath = solutionPath;
        return Task.FromResult(new WorkspaceInfo(
            solutionPath,
            [new ProjectInfo("TestProject", "TestProject.csproj", 5)]));
    }

    public Task<DiagnosticsResponse> GetDiagnosticsAsync(string? projectName, CancellationToken ct)
    {
        var diagnostics = projectName is null
            ? new List<DiagnosticResult>
            {
                new("CS0001", "Test error", "Error", "File.cs", 10, 5),
                new("CS0002", "Test warning", "Warning", "File2.cs", 20, 3),
            }
            : new List<DiagnosticResult>
            {
                new("CS0001", "Test error", "Error", "File.cs", 10, 5),
            };

        return Task.FromResult(new DiagnosticsResponse(diagnostics));
    }

    public Task<SemanticSearchResponse> SemanticSearchAsync(string symbolName, CancellationToken ct)
    {
        if (symbolName == "NotFound")
        {
            return Task.FromResult(new SemanticSearchResponse(symbolName, "Unknown", []));
        }

        return Task.FromResult(new SemanticSearchResponse(
            symbolName,
            "Method",
            [new SymbolReference("File.cs", 10, 5, "MyClass.MyMethod")]));
    }

    public Task<AstResponse> GetAstAsync(string filePath, int maxDepth, CancellationToken ct)
    {
        return Task.FromResult(new AstResponse(
            filePath,
            new AstNode("ClassDeclaration", "MyClass", null, "public", 1, 20, [])));
    }

    public Task<RenameResponse> RenameAsync(string filePath, int line, int column, string newName, bool preview, CancellationToken ct)
    {
        return Task.FromResult(new RenameResponse(
            [new FileChange(filePath, "OldName", newName)],
            !preview));
    }

    public Task NotifyFileChangedAsync(string filePath, CancellationToken ct)
    {
        LastFileChangedPath = filePath;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
