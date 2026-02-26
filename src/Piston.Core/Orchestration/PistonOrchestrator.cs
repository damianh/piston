using Piston.Core.Models;
using Piston.Core.Services;

namespace Piston.Core.Orchestration;

public sealed class PistonOrchestrator : IPistonOrchestrator
{
    private readonly IFileWatcherService _fileWatcher;
    private readonly IBuildService _buildService;
    private readonly ITestRunnerService _testRunner;
    private readonly PistonState _state;

    private CancellationTokenSource? _cts;
    private string? _solutionPath;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public PistonOrchestrator(
        IFileWatcherService fileWatcher,
        IBuildService buildService,
        ITestRunnerService testRunner,
        PistonState state)
    {
        _fileWatcher = fileWatcher;
        _buildService = buildService;
        _testRunner = testRunner;
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

        // Kick off an initial build+test run so results appear on startup
        // without waiting for the first file-change event.
        _ = TriggerRunAsync(solutionPath);

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

        // Fire-and-forget; cancellation of prior run handled inside TriggerRunAsync
        _ = TriggerRunAsync(_solutionPath);
    }

    private async Task TriggerRunAsync(string solutionPath)
    {
        // Issue a new CTS, cancelling whatever was running before
        var oldCts = _cts;
        var newCts = new CancellationTokenSource();
        _cts = newCts;
        oldCts?.Cancel();
        oldCts?.Dispose();

        // Serialize: only one pipeline runs at a time
        try
        {
            await _runLock.WaitAsync(newCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Superseded before we even started
        }

        try
        {
            var ct = newCts.Token;

            // --- Build ---
            _state.Phase = PistonPhase.Building;
            _state.NotifyChanged();

            BuildResult buildResult;
            try
            {
                buildResult = await _buildService.BuildAsync(solutionPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            _state.LastBuild = buildResult;

            if (ct.IsCancellationRequested) return;

            if (buildResult.Status == BuildStatus.Failed)
            {
                _state.Phase = PistonPhase.Error;
                _state.NotifyChanged();
                return;
            }

            // --- Test ---
            _state.Phase = PistonPhase.Testing;
            _state.NotifyChanged();

            IReadOnlyList<TestSuite> suites;
            try
            {
                suites = await _testRunner.RunTestsAsync(solutionPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested) return;

            _state.TestSuites = suites;
            _state.LastRunTime = DateTimeOffset.UtcNow;
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
