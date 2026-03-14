using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Orchestration;

public sealed class PistonOrchestrator : IPistonOrchestrator
{
    private readonly IFileWatcherService _fileWatcher;
    private readonly IBuildService _buildService;
    private readonly ITestRunnerService _testRunner;
    private readonly IImpactAnalyzer _impactAnalyzer;
    private readonly PistonState _state;

    private CancellationTokenSource? _cts;
    private string? _solutionPath;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public PistonOrchestrator(
        IFileWatcherService fileWatcher,
        IBuildService buildService,
        ITestRunnerService testRunner,
        IImpactAnalyzer impactAnalyzer,
        PistonState state)
    {
        _fileWatcher = fileWatcher;
        _buildService = buildService;
        _testRunner = testRunner;
        _impactAnalyzer = impactAnalyzer;
        _state = state;

        _fileWatcher.FileChanged += OnFileChanged;
    }

    public async Task StartAsync(string solutionPath)
    {
        _solutionPath = solutionPath;
        _state.SolutionPath = solutionPath;

        var solutionDir = Path.GetDirectoryName(solutionPath)
            ?? throw new ArgumentException("Cannot resolve solution directory.", nameof(solutionPath));

        _state.Phase = PistonPhase.Watching;
        _state.NotifyChanged();

        // Initialize the impact analyzer on a background thread
        _ = Task.Run(async () =>
        {
            try
            {
                await _impactAnalyzer.InitializeAsync(solutionPath, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Graph load failure is non-fatal — falls back to full runs
            }
        });

        _fileWatcher.Start(solutionDir);

        // Kick off an initial build+test run immediately on startup.
        _ = TriggerRunAsync(solutionPath, null);
    }

    public async Task ForceRunAsync()
    {
        if (_solutionPath is null) return;
        await TriggerRunAsync(_solutionPath, null);
    }

    public void Stop()
    {
        _fileWatcher.Stop();
        _cts?.Cancel();

        _state.Phase = PistonPhase.Idle;
        _state.NotifyChanged();
    }

    private void OnFileChanged(FileChangeBatch batch)
    {
        if (_solutionPath is null) return;
        _state.LastFileChangeTime = batch.Timestamp;
        _state.LastChangedFiles = batch.Changes.Select(e => e.FilePath).ToList();
        _ = TriggerRunAsync(_solutionPath, batch);
    }

    private async Task TriggerRunAsync(string solutionPath, FileChangeBatch? batch)
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

            // ── Analyzing ─────────────────────────────────────────────────────
            _state.Phase = PistonPhase.Analyzing;
            _state.NotifyChanged();

            ImpactAnalysisResult impactResult;
            try
            {
                impactResult = batch is not null
                    ? _impactAnalyzer.Analyze(batch.Changes)
                    : _impactAnalyzer.AnalyzeFullRun();
            }
            catch (OperationCanceledException)
            {
                _state.Phase = PistonPhase.Watching;
                _state.NotifyChanged();
                return;
            }

            _state.AffectedProjects = impactResult.AffectedProjectPaths.Count > 0
                ? impactResult.AffectedProjectPaths
                : null;
            _state.AffectedTestProjects = impactResult.AffectedTestProjectPaths.Count > 0
                ? impactResult.AffectedTestProjectPaths
                : null;

            if (ct.IsCancellationRequested)
            {
                _state.Phase = PistonPhase.Watching;
                _state.NotifyChanged();
                return;
            }

            // ── Build ─────────────────────────────────────────────────────────
            _state.Phase = PistonPhase.Building;
            _state.NotifyChanged();

            IReadOnlyList<string>? buildTargets = impactResult.IsFullRun ? null : impactResult.AffectedProjectPaths;

            BuildResult buildResult;
            try
            {
                buildResult = await _buildService.BuildAsync(solutionPath, buildTargets, ct).ConfigureAwait(false);
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
                ScheduleGraphRebuildIfNeeded(impactResult);
                return;
            }

            // ── Test ──────────────────────────────────────────────────────────
            _state.Phase = PistonPhase.Testing;

            IReadOnlyList<string>? testTargets = impactResult.IsFullRun ? null : impactResult.AffectedTestProjectPaths;

            // Seed InProgressSuites — for selective runs, only seed the affected suites
            var suitesToSeed = testTargets is not null
                ? _state.TestSuites.Where(s => IsSuiteAffected(s, testTargets)).ToList()
                : _state.TestSuites;

            _state.InProgressSuites = SeedAsRunning(suitesToSeed);
            _state.TotalExpectedTests = suitesToSeed.SelectMany(s => s.Tests).Count();
            _state.CompletedTests = 0;

            _state.NotifyChanged();

            var testStart = DateTimeOffset.UtcNow;

            void OnProgress(IReadOnlyList<TestSuite> liveSuites)
            {
                _state.InProgressSuites = MergeProgress(suitesToSeed, liveSuites);
                _state.CompletedTests = _state.InProgressSuites
                    .SelectMany(s => s.Tests)
                    .Count(t => t.Status != TestStatus.Running);
                _state.NotifyChanged();
            }

            IReadOnlyList<TestSuite> newSuites;
            try
            {
                var result = await _testRunner.RunTestsAsync(
                    solutionPath,
                    testTargets,
                    _state.TestFilter,
                    OnProgress,
                    ct).ConfigureAwait(false);
                newSuites = result.Suites;
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

            // Merge selective results with preserved results from untouched test projects
            _state.TestSuites = impactResult.IsFullRun
                ? newSuites
                : MergeTestSuites(_state.TestSuites, newSuites);

            _state.InProgressSuites = [];
            _state.LastRunTime = DateTimeOffset.UtcNow;
            _state.LastTestDuration = DateTimeOffset.UtcNow - testStart;

            // Compute how many tests are verified since the last file change
            if (_state.LastFileChangeTime.HasValue)
            {
                var changeTime = _state.LastFileChangeTime.Value;
                _state.VerifiedSinceChangeCount = newSuites
                    .Where(s => s.Timestamp >= changeTime)
                    .SelectMany(s => s.Tests)
                    .Count();
            }
            else
            {
                _state.VerifiedSinceChangeCount = _state.TestSuites.SelectMany(s => s.Tests).Count();
            }

            _state.Phase = PistonPhase.Watching;
            _state.NotifyChanged();

            ScheduleGraphRebuildIfNeeded(impactResult);
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

    private void ScheduleGraphRebuildIfNeeded(ImpactAnalysisResult impactResult)
    {
        if (impactResult.RequiresGraphRebuild)
            _impactAnalyzer.InvalidateGraph();
    }

    /// <summary>
    /// Determines whether a test suite is in the set of affected test project paths.
    /// Matches by suite name suffix against project directory/name heuristic.
    /// Falls back to including all suites when uncertain.
    /// </summary>
    private static bool IsSuiteAffected(TestSuite suite, IReadOnlyList<string> testProjectPaths)
    {
        // If we have test project paths, check if the suite's source matches any
        return testProjectPaths.Any(p =>
        {
            var projectDir = Path.GetDirectoryName(p);
            return projectDir is not null &&
                suite.Name.Contains(Path.GetFileNameWithoutExtension(p), StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Merges new suite results from a selective run into the existing full suite list.
    /// Suites from the new run replace their counterparts (matched by name).
    /// Suites not in the new run are preserved as-is.
    /// </summary>
    private static IReadOnlyList<TestSuite> MergeTestSuites(
        IReadOnlyList<TestSuite> existing,
        IReadOnlyList<TestSuite> newResults)
    {
        if (newResults.Count == 0) return existing;
        if (existing.Count == 0) return newResults;

        var newByName = newResults.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        // Replace existing suites that appear in new results; add any new ones not in existing
        var merged = existing
            .Select(s => newByName.TryGetValue(s.Name, out var updated) ? updated : s)
            .ToList();

        foreach (var newSuite in newResults)
        {
            if (!existing.Any(s => string.Equals(s.Name, newSuite.Name, StringComparison.OrdinalIgnoreCase)))
                merged.Add(newSuite);
        }

        return merged;
    }

    /// <summary>
    /// Returns a copy of <paramref name="suites"/> with every test status set to Running.
    /// Used to seed the live overlay at the start of a test phase.
    /// </summary>
    private static IReadOnlyList<TestSuite> SeedAsRunning(IEnumerable<TestSuite> suites)
    {
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
        IEnumerable<TestSuite> lastSuites,
        IReadOnlyList<TestSuite> liveSuites)
    {
        var lastSuiteList = lastSuites as IReadOnlyList<TestSuite> ?? lastSuites.ToList();

        if (liveSuites.Count == 0) return SeedAsRunning(lastSuiteList);

        // Build a lookup from the flat live list
        var liveByFqn = liveSuites
            .SelectMany(s => s.Tests)
            .ToDictionary(t => t.FullyQualifiedName, t => t.Status, StringComparer.Ordinal);

        if (!lastSuiteList.Any())
        {
            // No prior results — just show the live tests grouped into one synthetic suite
            return liveSuites;
        }

        return lastSuiteList
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
