using Piston.Roslyn.Messages;

namespace Piston.Roslyn;

public interface IRoslynWorkspace : IAsyncDisposable
{
    bool IsLoaded { get; }

    Task<WorkspaceInfo> LoadAsync(string solutionPath, CancellationToken ct);
}
