using Piston.Roslyn.Messages;

namespace Piston.Roslyn;

public interface IRoslynWorkspace : IAsyncDisposable
{
    bool IsLoaded { get; }

    Task<WorkspaceInfo> LoadAsync(string solutionPath, CancellationToken ct);

    Task<DiagnosticsResponse> GetDiagnosticsAsync(string? projectName, CancellationToken ct);

    Task<SemanticSearchResponse> SemanticSearchAsync(string symbolName, CancellationToken ct);

    Task<AstResponse> GetAstAsync(string filePath, int maxDepth, CancellationToken ct);

    Task<RenameResponse> RenameAsync(string filePath, int line, int column, string newName, bool preview, CancellationToken ct);

    Task NotifyFileChangedAsync(string filePath, CancellationToken ct);
}
