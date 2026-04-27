using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Roslyn.Messages;

namespace Piston.Roslyn;

internal sealed class RoslynWorkspaceProxy : IRoslynWorkspace
{
    private readonly RoslynWorkerProcess _worker = new();
    private bool _isLoaded;

    public bool IsLoaded => _isLoaded;

    public async Task<WorkspaceInfo> LoadAsync(string solutionPath, CancellationToken ct)
    {
        await _worker.EnsureStartedAsync(ct).ConfigureAwait(false);

        var @params = JsonSerializer.SerializeToNode(
            new LoadWorkspaceParams(solutionPath),
            RoslynJsonContext.Default.LoadWorkspaceParams);

        var result = await _worker.SendRequestAsync(RoslynMethods.LoadWorkspace, @params, ct)
            .ConfigureAwait(false);

        var info = result.Deserialize(RoslynJsonContext.Default.WorkspaceInfo)
            ?? throw new InvalidOperationException("Worker returned null workspace info.");

        _isLoaded = true;
        return info;
    }

    public async Task<DiagnosticsResponse> GetDiagnosticsAsync(string? projectName, CancellationToken ct)
    {
        var @params = JsonSerializer.SerializeToNode(
            new GetDiagnosticsParams(projectName),
            RoslynJsonContext.Default.GetDiagnosticsParams);

        var result = await _worker.SendRequestAsync(RoslynMethods.GetDiagnostics, @params, ct)
            .ConfigureAwait(false);

        return result.Deserialize(RoslynJsonContext.Default.DiagnosticsResponse)
            ?? throw new InvalidOperationException("Worker returned null diagnostics response.");
    }

    public async Task<SemanticSearchResponse> SemanticSearchAsync(string symbolName, CancellationToken ct)
    {
        var @params = JsonSerializer.SerializeToNode(
            new SemanticSearchParams(symbolName),
            RoslynJsonContext.Default.SemanticSearchParams);

        var result = await _worker.SendRequestAsync(RoslynMethods.SemanticSearch, @params, ct)
            .ConfigureAwait(false);

        return result.Deserialize(RoslynJsonContext.Default.SemanticSearchResponse)
            ?? throw new InvalidOperationException("Worker returned null semantic search response.");
    }

    public async Task<AstResponse> GetAstAsync(string filePath, int maxDepth, CancellationToken ct)
    {
        var @params = JsonSerializer.SerializeToNode(
            new GetAstParams(filePath, maxDepth),
            RoslynJsonContext.Default.GetAstParams);

        var result = await _worker.SendRequestAsync(RoslynMethods.GetAst, @params, ct)
            .ConfigureAwait(false);

        return result.Deserialize(RoslynJsonContext.Default.AstResponse)
            ?? throw new InvalidOperationException("Worker returned null AST response.");
    }

    public async Task<RenameResponse> RenameAsync(string filePath, int line, int column, string newName, bool preview, CancellationToken ct)
    {
        var @params = JsonSerializer.SerializeToNode(
            new RenameParams(filePath, line, column, newName, preview),
            RoslynJsonContext.Default.RenameParams);

        var result = await _worker.SendRequestAsync(RoslynMethods.Rename, @params, ct)
            .ConfigureAwait(false);

        return result.Deserialize(RoslynJsonContext.Default.RenameResponse)
            ?? throw new InvalidOperationException("Worker returned null rename response.");
    }

    public async Task NotifyFileChangedAsync(string filePath, CancellationToken ct)
    {
        var @params = JsonSerializer.SerializeToNode(
            new FileChangedParams(filePath),
            RoslynJsonContext.Default.FileChangedParams);

        await _worker.SendRequestAsync(RoslynMethods.FileChanged, @params, ct)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_worker.IsRunning)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _worker.SendRequestAsync(RoslynMethods.Shutdown, null, cts.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // best-effort graceful shutdown
            }
        }

        await _worker.DisposeAsync().ConfigureAwait(false);
    }
}
