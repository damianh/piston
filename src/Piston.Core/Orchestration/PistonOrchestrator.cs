using Piston.Core.Models;
using Piston.Core.Services;

namespace Piston.Core.Orchestration;

public sealed class PistonOrchestrator : IPistonOrchestrator
{
    private readonly IFileWatcherService _fileWatcher;
    private readonly IBuildService _buildService;
    private readonly PistonState _state;

    private CancellationTokenSource? _cts;
    private string? _solutionPath;
    private Task _currentRun = Task.CompletedTask;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public PistonOrchestrator(
        IFileWatcherService fileWatcher,
        IBuildService buildService,
        PistonState state)
    {
        _fileWatcher = fileWatcher;
        _buildService = buildService;
        _state = state;

        _fileWatcher.FileChanged += OnFileChanged;
    }

    public Task StartAsync(string solutionPath)
    {
        _solutionPath = solutionPath;
        _state.SolutionPath = solutionPath;

        var solutionDir = Path.GetDirectoryName(solutionPath)
            ?? throw new ArgumentException("Cannot resolve solution directory.", nameof(solutionPath));

        _state.Phase = PistonPhase.Watching;
        _state.NotifyChanged();

        _fileWatcher.Start(solutionDir);
        return Task.CompletedTask;
    }

    public async Task ForceRunAsync()
    {
        if (_solutionPath is null) return;
        await TriggerRunAsync(_solutionPath);
    }

    public void Stop()
    {
        _fileWatcher.Stop();
        _cts?.Cancel();

        _state.Phase = PistonPhase.Idle;
        _state.NotifyChanged();
    }

    private void OnFileChanged(FileChangeEvent evt)
    {
        if (_solutionPath is null) return;

        // Fire-and-forget; cancellation of prior run happens inside TriggerRunAsync
        _ = TriggerRunAsync(_solutionPath);
    }

    private async Task TriggerRunAsync(string solutionPath)
    {
        // Cancel any in-progress run
        var oldCts = _cts;
        var newCts = new CancellationTokenSource();
        _cts = newCts;
        oldCts?.Cancel();

        // Serialize runs so only one executes at a time
        await _runLock.WaitAsync(newCts.Token).ConfigureAwait(false);
        try
        {
            if (newCts.Token.IsCancellationRequested) return;

            // --- Build phase ---
            _state.Phase = PistonPhase.Building;
            _state.NotifyChanged();

            var buildResult = await _buildService.BuildAsync(solutionPath, newCts.Token).ConfigureAwait(false);
            _state.LastBuild = buildResult;

            if (newCts.Token.IsCancellationRequested) return;

            if (buildResult.Status == BuildStatus.Failed)
            {
                _state.Phase = PistonPhase.Error;
                _state.NotifyChanged();
                return;
            }

            // TestRunnerService will be wired in Phase 3 — for now return to Watching
            _state.Phase = PistonPhase.Watching;
            _state.NotifyChanged();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer run — silent
        }
        finally
        {
            _runLock.Release();
        }
    }

    public void Dispose()
    {
        _fileWatcher.FileChanged -= OnFileChanged;
        _fileWatcher.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _runLock.Dispose();
    }
}
