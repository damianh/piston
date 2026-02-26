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

        // Kick off an initial build+test run immediately on startup.
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

            // ── Build ─────────────────────────────────────────────────────
            _state.Phase = PistonPhase.Building;
            _state.NotifyChanged();

            BuildResult buildResult;
            try
            {
                buildResult = await _buildService.BuildAsync(solutionPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _state.Phase = PistonPhase.Watching;
                _state.NotifyChanged();
                return;
            }

            _state.LastBuild = buildResult;
            _state.LastBuildDuration = buildResult.Duration;

            if (ct.IsCancellationRequested)
            {
                _state.Phase = PistonPhase.Watching;
                _state.NotifyChanged();
                return;
            }

            if (buildResult.Status == BuildStatus.Failed)
            {
                _state.Phase = PistonPhase.Error;
                _state.NotifyChanged();
                return;
            }

            // ── Test ──────────────────────────────────────────────────────
            _state.Phase = PistonPhase.Testing;

            // Seed InProgressSuites with last known results, all marked as Running.
            // This gives immediate "everything is running" feedback in the tree.
            _state.InProgressSuites = SeedAsRunning(_state.TestSuites);
            _state.NotifyChanged();

            var testStart = DateTimeOffset.UtcNow;

            void OnProgress(IReadOnlyList<TestSuite> liveSuites)
            {
                // Merge live stdout results into the last known suites so that tests
                // not yet reported still show their previous (Running) status.
                _state.InProgressSuites = MergeProgress(_state.TestSuites, liveSuites);
                _state.NotifyChanged();
            }

            IReadOnlyList<TestSuite> suites;
            try
            {
                var result = await _testRunner.RunTestsAsync(
                    solutionPath,
                    _state.TestFilter,
                    OnProgress,
                    ct).ConfigureAwait(false);
                suites = result.Suites;
                _state.LastTestRunnerError = result.RunnerError;
            }
            catch (OperationCanceledException)
            {
                _state.Phase = PistonPhase.Watching;
                _state.NotifyChanged();
                return;
            }

            if (ct.IsCancellationRequested)
            {
                _state.Phase = PistonPhase.Watching;
                _state.NotifyChanged();
                return;
            }

            _state.TestSuites = suites;
            _state.InProgressSuites = [];
            _state.LastRunTime = DateTimeOffset.UtcNow;
            _state.LastTestDuration = DateTimeOffset.UtcNow - testStart;
            _state.Phase = PistonPhase.Watching;
            _state.NotifyChanged();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer run — reset phase cleanly
            _state.Phase = PistonPhase.Watching;
            _state.NotifyChanged();
        }
        finally
        {
            _runLock.Release();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a copy of <paramref name="suites"/> with every test status set to Running.
    /// Used to seed the live overlay at the start of a test phase.
    /// </summary>
    private static IReadOnlyList<TestSuite> SeedAsRunning(IReadOnlyList<TestSuite> suites)
    {
        if (suites.Count == 0) return [];

        return suites
            .Select(s => s with
            {
                Tests = s.Tests
                    .Select(t => t with { Status = TestStatus.Running })
                    .ToList()
            })
            .ToList();
    }

    /// <summary>
    /// Merges live stdout progress into the last known suite results.
    /// Tests reported by stdout get their new status; unreported tests keep their
    /// Running seed status.
    /// </summary>
    private static IReadOnlyList<TestSuite> MergeProgress(
        IReadOnlyList<TestSuite> lastSuites,
        IReadOnlyList<TestSuite> liveSuites)
    {
        if (liveSuites.Count == 0) return SeedAsRunning(lastSuites);

        // Build a lookup from the flat live list
        var liveByFqn = liveSuites
            .SelectMany(s => s.Tests)
            .ToDictionary(t => t.FullyQualifiedName, t => t.Status, StringComparer.Ordinal);

        if (lastSuites.Count == 0)
        {
            // No prior results — just show the live tests grouped into one synthetic suite
            return liveSuites;
        }

        return lastSuites
            .Select(s => s with
            {
                Tests = s.Tests
                    .Select(t => liveByFqn.TryGetValue(t.FullyQualifiedName, out var liveStatus)
                        ? t with { Status = liveStatus }
                        : t with { Status = TestStatus.Running })
                    .ToList()
            })
            .ToList();
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
