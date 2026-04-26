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
