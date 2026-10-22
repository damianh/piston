// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Execution;
using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Piston.Engine.Verification;

namespace Piston.Engine.Orchestration;

public sealed class PistonOrchestrator : IPistonOrchestrator, IAsyncDisposable
{
    private readonly IFileWatcherService _fileWatcher;
    private readonly IBuildService _buildService;
    private readonly ITestRunnerService _testRunner;
    private readonly IImpactAnalyzer _impactAnalyzer;
    private readonly PistonState _state;
    private readonly ICoverageStore? _coverageStore;
    private readonly ICoverageProcessor? _coverageProcessor;
    private readonly bool _coverageEnabled;
    private readonly IActivityEventSink _activitySink;
    private readonly IExecutionAdmission _admission;

    private CancellationTokenSource? _cts;
    private volatile bool _stopping;
    private readonly object _lifecycleLock = new();
    private int _activePipelines;
    private TaskCompletionSource? _pipelinesDrained;
    private Task? _disposeTask;
    private bool _disposed;

    // Queue position of a run superseded while queued for machine admission; guarded by _runLock.
    private long? _inheritedQueueSequence;
    private string? _solutionPath;
    private Task? _graphInitTask;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly SemaphoreSlim _resetLock = new(1, 1);
    private readonly SemaphoreSlim _coverageLock = new(1, 1);
    private readonly string _engineInstanceId = Guid.NewGuid().ToString("N");
    private readonly object _runRecordsLock = new();
    private readonly Dictionary<string, TaskCompletionSource<TestRunRecord>> _runWaiters = new(StringComparer.Ordinal);
    private long _runSequence;
    private long _fileChangeGeneration;

    // The last successful build whose run finished Current; guarded by _runLock.
    private BuildBaseline? _buildBaseline;

    /// <summary>Maximum runs reused, awaited or requested by one <see cref="VerifyAsync"/> call.</summary>
    internal int MaxVerificationAttempts { get; set; } = 3;

    /// <summary>Longest time <see cref="VerifyAsync"/> waits for debounced watcher changes to be raised.</summary>
    internal TimeSpan VerificationSettleTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Resolves the .NET SDK version recorded in run context; null when unknown.</summary>
    /// <summary>The SDK version <c>dotnet</c> selects in the given directory.</summary>
    internal Func<string, string?>? SdkVersionProvider { get; set; }

    public PistonOrchestrator(
        IFileWatcherService fileWatcher,
        IBuildService buildService,
        ITestRunnerService testRunner,
        IImpactAnalyzer impactAnalyzer,
        PistonState state)
        : this(fileWatcher, buildService, testRunner, impactAnalyzer, state,
               null, null, false, NullActivityEventSink.Instance)
    {
    }

    internal PistonOrchestrator(
        IFileWatcherService fileWatcher,
        IBuildService buildService,
        ITestRunnerService testRunner,
        IImpactAnalyzer impactAnalyzer,
        PistonState state,
        ICoverageStore? coverageStore,
        ICoverageProcessor? coverageProcessor,
        bool coverageEnabled)
        : this(fileWatcher, buildService, testRunner, impactAnalyzer, state,
               coverageStore, coverageProcessor, coverageEnabled, NullActivityEventSink.Instance)
    {
    }

    internal PistonOrchestrator(
        IFileWatcherService fileWatcher,
        IBuildService buildService,
        ITestRunnerService testRunner,
        IImpactAnalyzer impactAnalyzer,
        PistonState state,
        ICoverageStore? coverageStore,
        ICoverageProcessor? coverageProcessor,
        bool coverageEnabled,
        IActivityEventSink activitySink)
        : this(fileWatcher, buildService, testRunner, impactAnalyzer, state,
               coverageStore, coverageProcessor, coverageEnabled, activitySink, UnboundedExecutionAdmission.Instance)
    {
    }

    internal PistonOrchestrator(
        IFileWatcherService fileWatcher,
        IBuildService buildService,
        ITestRunnerService testRunner,
        IImpactAnalyzer impactAnalyzer,
        PistonState state,
        ICoverageStore? coverageStore,
        ICoverageProcessor? coverageProcessor,
        bool coverageEnabled,
        IActivityEventSink activitySink,
        IExecutionAdmission admission)
    {
        _fileWatcher = fileWatcher;
        _buildService = buildService;
        _testRunner = testRunner;
        _impactAnalyzer = impactAnalyzer;
        _state = state;
        _coverageStore = coverageStore;
        _coverageProcessor = coverageProcessor;
        _coverageEnabled = coverageEnabled;
        _activitySink = activitySink;
        _admission = admission;

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

        // Initialize the coverage store when enabled
        if (_coverageEnabled && _coverageStore is not null)
        {
            try
            {
                await _coverageStore.InitializeAsync(solutionDir).ConfigureAwait(false);
                _state.HasCoverageData = false;
            }
            catch
            {
                // Coverage store initialization failure is non-fatal
            }
        }

        // Initialize the impact analyzer on a background thread.
        // Store the task so TriggerRunAsync can await it before using the graph.
        _graphInitTask = Task.Run(async () =>
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
        _ = TriggerRunAsync(solutionPath, null, TestRunTrigger.Startup);
    }

    public async Task ForceRunAsync() => await RequestRunAsync().ConfigureAwait(false);

    /// <summary>
    /// Re-initialises the engine in-process: cancels the active run, clears results and coverage,
    /// reloads the solution graph, restarts file monitoring and starts a full run, which re-discovers
    /// every test project. Returns once that run is requested; the filter and run history are kept.
    /// </summary>
    public async Task ResetAsync()
    {
        var solutionPath = _solutionPath ?? throw new InvalidOperationException("Engine has not been started.");
        var solutionDir = Path.GetDirectoryName(solutionPath)
            ?? throw new InvalidOperationException("Cannot resolve solution directory.");

        // Counted as a pipeline so disposal waits for it instead of disposing the locks it holds.
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activePipelines++ == 0)
            {
                _pipelinesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        try
        {
            await _resetLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _fileWatcher.Stop();
                lock (_lifecycleLock)
                {
                    _stopping = _disposed;
                    _cts?.Cancel();
                }

                // A pipeline holds the run lock until it finishes, so holding it here means no run
                // observes a half-reset state; the next run then waits for the reloaded graph.
                await _runLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    ClearResults(solutionDir);
                    ResetRunState();
                    _impactAnalyzer.InvalidateGraph();
                    _graphInitTask = Task.Run(async () =>
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
                }
                finally
                {
                    _ = _runLock.Release();
                }

                // Disposal disposes the watcher under this lock, so a restart cannot outlive it.
                lock (_lifecycleLock)
                {
                    if (!_disposed)
                    {
                        _fileWatcher.Start(solutionDir);
                    }
                }
            }
            finally
            {
                _ = _resetLock.Release();
            }

            _ = TriggerRunAsync(solutionPath, null, TestRunTrigger.Forced);
        }
        finally
        {
            lock (_lifecycleLock)
            {
                if (--_activePipelines == 0)
                {
                    _pipelinesDrained!.SetResult();
                }
            }
        }
    }

    // Resets the per-run state shown by clients; caller holds the run lock.
    private void ResetRunState()
    {
        _state.Phase = PistonPhase.Watching;
        _state.InProgressSuites = [];
        _state.LastBuild = null;
        _state.LastBuildDuration = null;
        _buildBaseline = null;
        _state.LastTestDuration = null;
        _state.LastTestRunnerError = null;
        _state.TotalExpectedTests = 0;
        _state.CompletedTests = 0;
        _state.TotalTestProjects = 0;
        _state.CompletedTestProjects = 0;
        _state.TotalBuildProjects = 0;
        _state.CompletedBuildProjects = 0;
        _state.LastBuiltProject = null;
        _state.ProjectStatuses = new Dictionary<string, ProjectRunStatus>();
        _state.AffectedProjects = null;
        _state.AffectedTestProjects = null;
        _state.LastFileChangeTime = null;
        _state.LastChangedFiles = null;
        _state.NotifyChanged();
    }

    /// <summary>
    /// Requests a full-scope run and returns its provenance record once it reaches a terminal state.
    /// Returns null when the engine has not been started.
    /// </summary>
    public async Task<TestRunRecord?> RequestRunAsync()
    {
        if (_solutionPath is null)
        {
            return null;
        }

        return await TriggerRunAsync(_solutionPath, null, TestRunTrigger.Forced).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a run of the selected tests and returns once it is requested; the returned task completes
    /// with the run's record. Unlike file-change and forced runs it never supersedes another run.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The selection names no project, a project that is not a test project, or a test whose name contains a double quote.
    /// </exception>
    /// <exception cref="InvalidOperationException">The engine is not ready, or a run is already in progress.</exception>
    public Task<TestRunRecord> RunSelectedAsync(TestSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var solutionPath = _solutionPath ?? throw new InvalidOperationException("The engine has not been started.");
        if (selection.Projects.Count == 0)
        {
            throw new ArgumentException("Select at least one test project.", nameof(selection));
        }

        var allTestProjects = _impactAnalyzer.GetAllTestProjectPaths();
        if (allTestProjects.Count == 0)
        {
            throw new InvalidOperationException("Test projects are still loading.");
        }

        // Results are reported under the project's file name, so projects sharing one (e.g. One/Tests.csproj and
        // Two/Tests.csproj) appear as a single node; selecting it runs all of them.
        var paths = new List<string>();
        var projects = selection.Projects.Distinct(StringComparer.Ordinal).ToList();
        foreach (var name in projects)
        {
            var matches = allTestProjects
                .Where(p => string.Equals(TestProjectNames.SuiteName(p), name, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 0)
            {
                throw new ArgumentException($"'{name}' is not a test project in this solution.", nameof(selection));
            }

            paths.AddRange(matches);
        }

        var known = _state.TestSuites
            .Where(s => projects.Contains(s.Name, StringComparer.Ordinal))
            .SelectMany(s => s.Tests);
        return TriggerRunAsync(solutionPath, null, TestRunTrigger.Targeted,
            new SelectedRun(paths, TestSelectionFilter.Build(selection.Tests, known)));
    }

    /// <summary>
    /// Waits for the run with <paramref name="runId"/> to reach a terminal state. Returns the retained
    /// record immediately when already terminal, or null when the run is neither active nor retained.
    /// </summary>
    public async Task<TestRunRecord?> AwaitRunAsync(string runId, CancellationToken ct)
    {
        TaskCompletionSource<TestRunRecord>? waiter;
        lock (_runRecordsLock)
        {
            var finished = _state.RecentRuns.FirstOrDefault(r => r.RunId == runId);
            if (finished is not null)
            {
                return finished;
            }

            if (!_runWaiters.TryGetValue(runId, out waiter))
            {
                if (_state.CurrentRun?.RunId != runId)
                {
                    return null;
                }

                waiter = new TaskCompletionSource<TestRunRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
                _runWaiters[runId] = waiter;
            }
        }

        return await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Produces current full verification for the present source: reuses the newest qualifying
    /// terminal run, awaits an active full-scope run, or requests a forced full run. Retries a bounded
    /// number of times when the source changes or the run is superseded. Never reports a result that
    /// is not bound to the present source fingerprint as passing.
    /// </summary>
    /// <param name="timeout">Overall limit; on expiry the verdict is <see cref="VerificationVerdict.TimedOut"/> and any run continues.</param>
    /// <param name="ct">Caller cancellation; throws <see cref="OperationCanceledException"/> without cancelling the run.</param>
    public async Task<VerificationResult> VerifyAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_solutionPath is null)
        {
            return new VerificationResult(VerificationVerdict.Faulted, "Engine has not been started.",
                VerificationSource.None, 0, null, null);
        }

        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(_solutionPath)) ?? _solutionPath;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;

        var attempts = 0;
        VerificationResult? last = null;
        TestRunRecord? pending = null;
        try
        {
            while (true)
            {
                await WaitForWatcherToSettleAsync(token).ConfigureAwait(false);

                var current = SourceFingerprinter.TryCompute(solutionDir);
                if (_fileWatcher.MonitoringError is { } monitoringError)
                {
                    return new VerificationResult(VerificationVerdict.Faulted,
                        $"File monitoring is unreliable: {monitoringError} Restart the daemon before verifying.",
                        VerificationSource.None, attempts, null, current);
                }
                string? coverageIssue;
                IReadOnlyList<string> requiredTestProjects;
                try
                {
                    var coverage = await Task.Run(
                        () =>
                        {
                            var issue = _impactAnalyzer.GetVerificationCoverageIssue(_solutionPath, out var projects);
                            return (Issue: issue, Projects: projects);
                        }, CancellationToken.None)
                        .WaitAsync(token).ConfigureAwait(false);
                    coverageIssue = coverage.Issue;
                    requiredTestProjects = coverage.Projects;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new VerificationResult(VerificationVerdict.Faulted,
                        $"Could not evaluate build-input coverage: {ex.GetType().Name}: {ex.Message}",
                        VerificationSource.None, attempts, null, current);
                }

                if (_fileWatcher.MonitoringError is { } decisionMonitoringError)
                {
                    return new VerificationResult(VerificationVerdict.Faulted,
                        $"File monitoring is unreliable: {decisionMonitoringError} Restart the daemon before verifying.",
                        VerificationSource.None, attempts, null, current);
                }

                if (coverageIssue is not null)
                {
                    return new VerificationResult(VerificationVerdict.NotFullScope,
                        coverageIssue, VerificationSource.None, attempts, null, current);
                }

                TestRunRecord? active;
                IReadOnlyList<TestRunRecord> recent;
                lock (_runRecordsLock)
                {
                    active = _state.CurrentRun;
                    recent = _state.RecentRuns;
                }

                var decision = VerificationPolicy.Decide(
                    current, active, recent, _state.TestFilter, _fileWatcher.HasPendingChanges, requiredTestProjects);
                if (decision.Action == VerificationAction.Final)
                {
                    return new VerificationResult(decision.Verdict!.Value, decision.Reason,
                        VerificationSource.None, attempts, null, current);
                }

                attempts++;
                TestRunRecord? run;
                VerificationSource source;
                switch (decision.Action)
                {
                    case VerificationAction.Reuse:
                        run = decision.Run;
                        source = VerificationSource.Reused;
                        break;
                    case VerificationAction.AwaitActive:
                        pending = decision.Run;
                        run = await AwaitRunAsync(decision.Run!.RunId, token).ConfigureAwait(false);
                        source = VerificationSource.AwaitedActive;
                        break;
                    default:
                        source = VerificationSource.Forced;
                        try
                        {
                            // Run the request off the caller so synchronous pipeline work (e.g. fingerprinting
                            // a large tree) cannot delay the timeout.
                            run = await Task.Run(RequestRunAsync, CancellationToken.None).WaitAsync(token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            return new VerificationResult(VerificationVerdict.Faulted,
                                $"{ex.GetType().Name}: {ex.Message}", source, attempts, _state.LastRun, current);
                        }
                        break;
                }

                pending = null;
                if (run is null)
                {
                    if (attempts >= MaxVerificationAttempts)
                    {
                        return new VerificationResult(VerificationVerdict.Stale,
                            $"The awaited run is no longer retained. Gave up after {attempts} attempt(s); " +
                            "no current full result exists for the present source.",
                            source, attempts, last?.Run, current);
                    }

                    continue;
                }

                var after = SourceFingerprinter.TryCompute(solutionDir);
                var evaluation = after != current
                    ? new VerificationEvaluation(VerificationVerdict.Stale, true,
                        "Source changed after build-input coverage was evaluated.")
                    : VerificationPolicy.Evaluate(run, after, requiredTestProjects);
                if (_fileWatcher.MonitoringError is { } afterMonitoringError)
                {
                    evaluation = new VerificationEvaluation(VerificationVerdict.Faulted, false,
                        $"File monitoring is unreliable: {afterMonitoringError} Restart the daemon before verifying.");
                }
                if (evaluation.Verdict == VerificationVerdict.Passed && _fileWatcher.HasPendingChanges)
                {
                    evaluation = new VerificationEvaluation(VerificationVerdict.Stale, true,
                        "File changes were observed while verifying the result.");
                }
                last = new VerificationResult(evaluation.Verdict, evaluation.Reason, source, attempts, run, after);
                if (!evaluation.Retryable || _stopping || attempts >= MaxVerificationAttempts)
                {
                    if (evaluation.Retryable && !_stopping && attempts >= MaxVerificationAttempts)
                    {
                        // Retries exhausted: whatever the last run's own status, no current full result exists.
                        last = last with
                        {
                            Verdict = VerificationVerdict.Stale,
                            Reason = $"{evaluation.Reason} Gave up after {attempts} attempt(s); the source may still be changing.",
                        };
                    }

                    return last;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var activeRun = pending ?? _state.CurrentRun;
            return new VerificationResult(VerificationVerdict.TimedOut,
                $"No current full result within {timeout.TotalSeconds:0.#}s" +
                (activeRun is null ? "." : $"; run {activeRun.RunId} is still {activeRun.Status} and continues.") +
                (last?.Reason is { } previous ? $" Previous attempt: {last.Verdict}: {previous}" : ""),
                last?.Source ?? VerificationSource.None, attempts, activeRun ?? last?.Run, last?.SourceFingerprint);
        }
    }

    private async Task WaitForWatcherToSettleAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + VerificationSettleTimeout;
        while (_fileWatcher.MonitoringError is null
            && _fileWatcher.HasPendingChanges && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    internal void ClearResults(string solutionDirectory)
    {
        _coverageLock.Wait();
        try
        {
            SqliteCoverageStore.Clear(solutionDirectory);
            _state.TestSuites = [];
            _state.LastRunTime = null;
            _state.HasCoverageData = false;
            _state.CoverageImpactDetail = null;
        }
        finally
        {
            _ = _coverageLock.Release();
        }
        _state.NotifyChanged();
    }

    /// <summary>Returns the coverage report, or null when coverage is disabled.</summary>
    internal CoverageReportSnapshot? GetCoverageReport()
    {
        if (_coverageStore is null)
        {
            return null;
        }

        IReadOnlyList<CoverageFileSummary> files;
        _coverageLock.Wait();
        try
        {
            files = _coverageStore.GetReportFiles();
        }
        finally
        {
            _ = _coverageLock.Release();
        }

        return new CoverageReportSnapshot([.. files.Select(WithCurrentStaleness)]);
    }

    /// <summary>
    /// Returns the coverage of <paramref name="filePath"/>, or null when coverage is disabled or the file
    /// was not measured.
    /// </summary>
    internal FileCoverageSnapshot? GetFileCoverage(string filePath)
    {
        if (_coverageStore is null)
        {
            return null;
        }

        CoverageFileSummary? file;
        IReadOnlyList<LineCoverage> lines;
        _coverageLock.Wait();
        try
        {
            file = _coverageStore.GetReportFile(filePath);
            lines = file is null ? [] : _coverageStore.GetReportLines(filePath);
        }
        finally
        {
            _ = _coverageLock.Release();
        }

        return file is null ? null : new FileCoverageSnapshot(WithCurrentStaleness(file), lines);
    }

    /// <summary>
    /// One run-granularity impact unit per test project. A project's unit is replaced only when all its tests ran
    /// cleanly; otherwise the run adds to what is stored.
    /// </summary>
    internal static IReadOnlyList<CoverageRunUnit> RunImpactUnits(
        IReadOnlyList<ProjectTestResult> projects,
        string solutionPath,
        IReadOnlyList<string> reportPaths,
        IReadOnlyList<string> testFqns,
        string? filter)
    {
        var filtered = !string.IsNullOrWhiteSpace(filter);
        if (projects.Count == 0)
        {
            return [new CoverageRunUnit(solutionPath, RunUnitKey, reportPaths, testFqns, Merge: true)];
        }

        return [.. projects.Select(p => new CoverageRunUnit(
            p.ProjectPath,
            RunUnitKey,
            p.CoverageReportPaths,
            [.. p.Suites.SelectMany(s => s.Tests).Select(t => t.FullyQualifiedName).Distinct(StringComparer.Ordinal)],
            Merge: filtered || p.Crashed || p.RunnerError is not null))];
    }

    private const string RunUnitKey = "run";

    /// <summary>Flags a file stale when its content no longer matches what was measured, e.g. after edits while stopped.</summary>
    private static CoverageFileSummary WithCurrentStaleness(CoverageFileSummary file) =>
        file.IsStale || file.ContentHash is null
            || string.Equals(file.ContentHash, CoverageReportFile.ComputeContentHash(file.FilePath), StringComparison.Ordinal)
            ? file
            : file with { IsStale = true };

    internal async Task ProcessCoverageAsync(
        long runId,
        IReadOnlyList<string> reportPaths,
        IReadOnlyList<string> testFqns,
        bool isFullRun = false,
        CoverageImpactOptions? impact = null)
    {
        var processor = _coverageProcessor
            ?? throw new InvalidOperationException("Coverage processing is not enabled.");
        var store = _coverageStore
            ?? throw new InvalidOperationException("Coverage storage is not enabled.");
        await _coverageLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var solutionDir = _solutionPath is null ? null : Path.GetDirectoryName(_solutionPath);
            await processor.ProcessCoverageAsync(runId, reportPaths, testFqns, store, isFullRun, solutionDir, impact)
                .ConfigureAwait(false);
            _state.HasCoverageData = true;
        }
        finally
        {
            _ = _coverageLock.Release();
        }
    }

    public void Stop()
    {
        _stopping = true;
        _fileWatcher.Stop();
        lock (_lifecycleLock)
        {
            _cts?.Cancel();
        }

        _state.Phase = PistonPhase.Idle;
        _state.NotifyChanged();
    }

    private void OnFileChanged(FileChangeBatch batch)
    {
        if (_solutionPath is null)
        {
            return;
        }

        _ = Interlocked.Increment(ref _fileChangeGeneration);
        _state.LastFileChangeTime = batch.Timestamp;
        _state.LastChangedFiles = batch.Changes.Select(e => e.FilePath).ToList();
        _activitySink.Emit(new FileChangesDetectedActivityEvent(
            _solutionPath, _state.LastChangedFiles));
        _ = TriggerRunAsync(_solutionPath, batch, TestRunTrigger.FileChange);
    }

    /// <summary>The test projects and filter of a <see cref="TestRunTrigger.Targeted"/> run.</summary>
    private sealed record SelectedRun(IReadOnlyList<string> TestProjectPaths, string? Filter);

    private Task<TestRunRecord> TriggerRunAsync(
        string solutionPath, FileChangeBatch? batch, TestRunTrigger trigger, SelectedRun? selection = null)
    {
        CancellationTokenSource cts;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return Task.FromResult(CompleteRun(CreateRunRecord(trigger), TestRunStatus.Cancelled, "Engine is shutting down."));
            }

            // Checked under the lock so a selected run cannot cancel a run that started meanwhile.
            if (selection is not null && _activePipelines > 0)
            {
                throw new InvalidOperationException("A test run is already in progress.");
            }

            if (_activePipelines++ == 0)
            {
                _pipelinesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            cts = new CancellationTokenSource();
            var oldCts = _cts;
            _cts = cts;
            oldCts?.Cancel();
        }
        return RunTrackedAsync(solutionPath, batch, trigger, selection, cts);
    }

    private async Task<TestRunRecord> RunTrackedAsync(
        string solutionPath, FileChangeBatch? batch, TestRunTrigger trigger, SelectedRun? selection, CancellationTokenSource cts)
    {
        try
        {
            return await RunPipelineAsync(solutionPath, batch, trigger, selection, cts).ConfigureAwait(false);
        }
        finally
        {
            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                }

                cts.Dispose();
                if (--_activePipelines == 0)
                {
                    _pipelinesDrained!.SetResult();
                }
            }
        }
    }

    private async Task<TestRunRecord> RunPipelineAsync(
        string solutionPath, FileChangeBatch? batch, TestRunTrigger trigger, SelectedRun? selection, CancellationTokenSource newCts)
    {
        var run = CreateRunRecord(trigger);

        // Serialize: only one pipeline runs at a time
        try
        {
            await _runLock.WaitAsync(newCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CompleteRun(run, TestRunStatus.Cancelled, _stopping
                ? "Engine stopped before the run started."
                : "Superseded by a newer run before it started.");
        }

        // Machine-wide admission is distinct from the per-daemon run lock: the lock serializes this
        // daemon's runs, admission shares the machine budget with other daemons and worktrees.
        IExecutionLease lease;
        var inheritedSequence = _inheritedQueueSequence;
        _inheritedQueueSequence = null;
        try
        {
            lease = await _admission.AdmitAsync(
                run.RunId,
                status =>
                {
                    run = run with { Status = TestRunStatus.Queued, Admission = status };
                    PublishActiveRun(run);
                },
                inheritedSequence,
                newCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // Keep the queue position for the run that superseded this one, unless the engine is stopping.
            if (!_stopping)
            {
                _inheritedQueueSequence = ex is ExecutionAdmissionCancelledException { QueueSequence: { } seq } ? seq : inheritedSequence;
            }

            _ = _runLock.Release();
            return CompleteRun(run, TestRunStatus.Cancelled,
                _stopping
                    ? "Engine stopped while waiting for machine-wide execution admission."
                    : "Superseded by a newer run while waiting for machine-wide execution admission.");
        }
        catch (Exception ex)
        {
            _ = _runLock.Release();
            _ = CompleteRun(run, TestRunStatus.Faulted, $"Execution admission failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? solutionPath;
        var changeGenerationAtStart = Interlocked.Read(ref _fileChangeGeneration);
        run = run with
        {
            Status = TestRunStatus.Running,
            Admission = lease.Status,
            StartedAt = DateTimeOffset.UtcNow,
            SourceBefore = SourceFingerprinter.TryCompute(solutionDir),
        };
        PublishActiveRun(run);

        BuildBaseline? builtBaseline = null;

        TestRunRecord Finish(TestRunStatus status, string? reason, bool computeAfter = true)
        {
            var finished = CompleteRun(run, status, reason, solutionDir, changeGenerationAtStart, computeAfter);

            // Outputs are reusable only if the sources were stable for the whole run.
            if (builtBaseline is not null && finished.Freshness == ResultFreshness.Current)
            {
                _buildBaseline = builtBaseline;
            }

            return finished;
        }

        try
        {
            var ct = newCts.Token;

            // Wait for the solution graph to finish loading before analyzing impact.
            // This prevents the race where AnalyzeFullRun() runs with a null graph,
            // producing empty project lists and routing the solution path to VSTest.
            if (_graphInitTask is { } initTask)
            {
                var log0 = DiagnosticLog.Instance;
                log0?.Write("Orchestrator", "Waiting for graph initialization...");
                await initTask.ConfigureAwait(false);
                log0?.Write("Orchestrator", "Graph initialization complete.");
                _graphInitTask = null;
            }

            // ── Analyzing ─────────────────────────────────────────────────────
            _state.Phase = PistonPhase.Analyzing;
            _state.NotifyChanged();

            var log = DiagnosticLog.Instance;
            log?.Write("Orchestrator", (batch, selection) switch
            {
                ({ } b, _) => $"TriggerRun: {b.Changes.Count} file change(s)",
                (_, { } s) => $"TriggerRun: {s.TestProjectPaths.Count} selected test project(s)",
                _ => "TriggerRun: full run (no batch)",
            });

            ImpactAnalysisResult impactResult;
            try
            {
                // Building a test project also builds the projects it references.
                impactResult = (batch, selection) switch
                {
                    ({ } b, _) => _impactAnalyzer.Analyze(b.Changes),
                    (_, { } s) => new ImpactAnalysisResult([], s.TestProjectPaths, RequiresGraphRebuild: false, IsFullRun: false),
                    _ => _impactAnalyzer.AnalyzeFullRun(),
                };
            }
            catch (OperationCanceledException)
            {
                _state.Phase = PistonPhase.Watching;
                _state.NotifyChanged();
                return Finish(TestRunStatus.Cancelled, "Cancelled during impact analysis.");
            }

            _state.AffectedProjects = impactResult.AffectedProjectPaths.Count > 0
                ? impactResult.AffectedProjectPaths
                : null;
            _state.AffectedTestProjects = impactResult.AffectedTestProjectPaths.Count > 0
                ? impactResult.AffectedTestProjectPaths
                : null;

            log?.Write("Orchestrator",
                $"Impact: fullRun={impactResult.IsFullRun} | " +
                $"affectedProjects={impactResult.AffectedProjectPaths.Count} | " +
                $"affectedTestProjects={impactResult.AffectedTestProjectPaths.Count}");
            foreach (var tp in impactResult.AffectedTestProjectPaths)
            {
                log?.Write("Orchestrator", $"  TestProject: {Path.GetFileName(tp)}");
            }

            var buildTargets = impactResult.IsFullRun ? null : impactResult.BuildTargetPaths;

            // When the graph is available, always enumerate individual test projects
            // so the composite strategy can route MTP projects to the MTP runner.
            // Only fall back to null (single-solution-target) when no graph is loaded.
            IReadOnlyList<string>? testTargets;
            if (impactResult.IsFullRun)
            {
                var allTestProjects = _impactAnalyzer.GetAllTestProjectPaths();
                testTargets = allTestProjects.Count > 0 ? allTestProjects : null;
            }
            else
            {
                testTargets = impactResult.AffectedTestProjectPaths;
            }

            // Build the effective test filter:
            //   - A selected run runs exactly its selection, ignoring the user's TestFilter
            //   - If Tier 3 produced FQNs, build a dotnet-test filter expression
            //   - If the user also has a TestFilter, AND the two together
            var userFilter = selection is null ? _state.TestFilter : null;
            var effectiveFilter = selection is null
                ? BuildEffectiveFilter(impactResult, userFilter)
                : selection.Filter;

            run = run with
            {
                Scope = impactResult.IsFullRun ? TestRunScope.Full : TestRunScope.Selective,
                Context = new TestRunContext(
                    SolutionPath: Path.GetFullPath(solutionPath),
                    TestProjects: testTargets?.ToList(),
                    BuildTargets: buildTargets?.ToList(),
                    UserFilter: string.IsNullOrWhiteSpace(userFilter) ? null : userFilter,
                    EffectiveFilter: string.IsNullOrWhiteSpace(effectiveFilter) ? null : effectiveFilter,
                    CoverageSelectedTestCount: impactResult.AffectedTestFqns is { Count: > 0 and <= 100 } selected
                        ? selected.Count
                        : null,
                    CoverageEnabled: _coverageEnabled,
                    BuildConfiguration: "Debug",
                    SdkVersion: SafeSdkVersion(solutionDir),
                    Runtime: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    OperatingSystem: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    WorkingDirectory: solutionDir,
                    TestWorkingDirectories: (testTargets ?? [Path.GetFullPath(solutionPath)])
                        .ToDictionary(
                            path => path,
                            path => _impactAnalyzer.GetMtpOutputPath(path) is not null
                                ? solutionDir
                                : TestProcessRunner.WorkingDirectoryFor(path) ?? Environment.CurrentDirectory,
                            StringComparer.Ordinal)),
            };
            if (run.IsFullScope)
            {
                run = run with { InputCoverageVerified = IsInputCoverageVerified(solutionPath) };
            }

            PublishActiveRun(run);

            if (ct.IsCancellationRequested)
            {
                _state.Phase = PistonPhase.Watching;
                _state.NotifyChanged();
                return Finish(TestRunStatus.Cancelled, "Cancelled after impact analysis.");
            }

            // ── Build ─────────────────────────────────────────────────────────
            // A targeted run reuses the outputs of the last build when nothing it could
            // depend on has changed since; dotnet's own no-op build still costs seconds.
            if (selection is not null && CanReuseLastBuild(solutionPath, run.SourceBefore, buildTargets))
            {
                log?.Write("Orchestrator", "Build skipped: sources unchanged since the last successful build.");
                _state.TotalBuildProjects = 0;
                _state.CompletedBuildProjects = 0;
                _state.LastBuiltProject = null;
                run = run with { Build = new BuildResult(BuildStatus.Skipped, [], [], TimeSpan.Zero) };
                PublishActiveRun(run);
            }
            else
            {
                _buildBaseline = null;

                // Progress counts projects by name as dotnet build reports their output.
                var expectedBuildProjects = _impactAnalyzer.GetBuildProjectPaths(buildTargets)
                    .Select(Path.GetFileNameWithoutExtension)
                    .OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var builtProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var buildProgressLock = new object();

                void OnProjectBuilt(string projectName)
                {
                    lock (buildProgressLock)
                    {
                        if (!builtProjects.Add(projectName))
                        {
                            return;
                        }

                        if (expectedBuildProjects.Count == 0 || expectedBuildProjects.Contains(projectName))
                        {
                            _state.CompletedBuildProjects++;
                        }

                        _state.LastBuiltProject = projectName;
                    }

                    _state.NotifyChanged();
                }

                _state.TotalBuildProjects = expectedBuildProjects.Count;
                _state.CompletedBuildProjects = 0;
                _state.LastBuiltProject = null;
                _state.Phase = PistonPhase.Building;
                _state.NotifyChanged();

                BuildResult buildResult;
                try
                {
                    buildResult = await _buildService.BuildAsync(solutionPath, buildTargets, OnProjectBuilt, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _state.Phase = PistonPhase.Watching;
                    _state.NotifyChanged();
                    return Finish(TestRunStatus.Cancelled, "Cancelled during build.");
                }

                _state.LastBuild = buildResult;
                _state.LastBuildDuration = buildResult.Duration;
                run = run with
                {
                    Build = buildResult with { Errors = buildResult.Errors.ToArray(), Warnings = buildResult.Warnings.ToArray() },
                };
                PublishActiveRun(run);

                // A superseded build is killed and reports Failed with no diagnostics; it must
                // not be published as a build failure.
                if (ct.IsCancellationRequested)
                {
                    _state.Phase = PistonPhase.Watching;
                    _state.NotifyChanged();
                    return Finish(TestRunStatus.Cancelled, "Cancelled after build.");
                }

                _activitySink.Emit(new BuildCompletedActivityEvent(
                    solutionPath,
                    buildResult.Status == BuildStatus.Succeeded,
                    buildResult.Duration.TotalMilliseconds,
                    buildResult.Errors));

                if (buildResult.Status == BuildStatus.Failed)
                {
                    _state.Phase = PistonPhase.Error;
                    _state.NotifyChanged();
                    ScheduleGraphRebuildIfNeeded(impactResult);
                    return Finish(TestRunStatus.BuildFailed,
                        $"Build failed with {buildResult.Errors.Count} error(s).", computeAfter: true);
                }

                // Outputs become reusable once the run finishes with stable sources (see Finish).
                if (run.SourceBefore is { } builtFrom && Interlocked.Read(ref _fileChangeGeneration) == changeGenerationAtStart)
                {
                    builtBaseline = new BuildBaseline(
                        Path.GetFullPath(solutionPath),
                        builtFrom,
                        buildTargets?.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal),
                        changeGenerationAtStart);
                }
            }

            // ── Test ──────────────────────────────────────────────────────────
            _state.Phase = PistonPhase.Testing;

            // No test project depends on the change. An empty list would make the
            // runner fall back to running the whole solution, so skip testing instead.
            if (testTargets is { Count: 0 })
            {
                log?.Write("Orchestrator", "Selective run has no affected test projects; skipping tests.");
                _state.Phase = PistonPhase.Watching;
                _state.NotifyChanged();
                ScheduleGraphRebuildIfNeeded(impactResult);
                return Finish(TestRunStatus.Completed,
                    "No affected test projects; tests were not run.", computeAfter: true);
            }

            // Update coverage impact detail for TUI
            if (impactResult.AffectedTestFqns is { Count: > 0 } fqns)
            {
                _state.CoverageImpactDetail = impactResult.SelectionReasons is { } reasons
                    ? $"Tier 3: {fqns.Count} test(s) (changed lines, {string.Join("/", reasons.Values.SelectMany(r => r).Select(r => r.Granularity).Distinct().Order(StringComparer.Ordinal))}-level)"
                    : $"Tier 3: {fqns.Count} test(s) (coverage)";
            }
            else
            {
                _state.CoverageImpactDetail = null;
            }

            // Seed InProgressSuites — for selective runs, only seed the affected suites
            var suitesToSeed = testTargets is not null
                ? _state.TestSuites.Where(s => IsSuiteAffected(s, testTargets)).ToList()
                : _state.TestSuites;

            // Per-project progress tracking
            _state.TotalTestProjects = testTargets?.Count ?? 0;
            _state.CompletedTestProjects = 0;
            var projectStatuses = testTargets is not null
                ? testTargets.ToDictionary(p => p, _ => Models.ProjectRunStatus.Pending)
                : new Dictionary<string, Models.ProjectRunStatus>();
            _state.ProjectStatuses = projectStatuses;

            // Live overlay inputs: discovered test lists and live/final results, keyed by project path.
            var discoveredByProject = new Dictionary<string, IReadOnlyList<TestResult>>(StringComparer.Ordinal);
            var liveByProject = new Dictionary<string, IReadOnlyList<TestSuite>>(StringComparer.Ordinal);
            // Projects whose authoritative results replaced their discovery placeholders.
            var settledProjects = new HashSet<string>(StringComparer.Ordinal);
            var liveResultsLock = new object();
            var testPhaseOver = false;
            var filtered = !string.IsNullOrWhiteSpace(effectiveFilter);

            // Caller holds liveResultsLock. Progress counts only tests in this run, not the known
            // results a filtered run keeps for display.
            void RefreshOverlay()
            {
                _state.ProjectStatuses = new Dictionary<string, Models.ProjectRunStatus>(projectStatuses);
                _state.InProgressSuites = ComposeTestOverlay(
                    suitesToSeed, discoveredByProject, liveByProject, filtered,
                    out var expectedTests, out var completedTests);
                _state.TotalExpectedTests = expectedTests;
                _state.CompletedTests = completedTests;
            }

            RefreshOverlay();

            // Run-local project outcomes (independent of the UI-facing ProjectStatuses).
            var projectOutcomes = new Dictionary<string, TestRunProjectOutcome>(StringComparer.Ordinal);
            foreach (var project in testTargets ?? [])
            {
                projectOutcomes[project] = new TestRunProjectOutcome(project, Models.ProjectRunStatus.Pending, 0);
            }

            _state.NotifyChanged();

            var testStart = DateTimeOffset.UtcNow;

            // Frequent updates (live progress, streamed discovery) recompose the overlay at most every
            // ProgressNotifyInterval; the trailing invocation guarantees the latest state is shown.
            using var throttledRefresh = new TrailingThrottle(
                () =>
                {
                    lock (liveResultsLock)
                    {
                        if (testPhaseOver)
                        {
                            return;
                        }

                        RefreshOverlay();
                    }

                    _state.NotifyChanged();
                },
                ProgressNotifyInterval);

            // Applies an overlay update unless the test phase has already finished.
            void UpdateOverlay(Action mutate, bool throttle = false)
            {
                lock (liveResultsLock)
                {
                    if (testPhaseOver)
                    {
                        return;
                    }

                    mutate();
                    if (!throttle)
                    {
                        RefreshOverlay();
                    }
                }

                // Signalled outside the lock: the throttled refresh takes it.
                if (throttle)
                {
                    throttledRefresh.Signal();
                }
                else
                {
                    _state.NotifyChanged();
                }
            }

            void OnProjectStarted(string projectPath) => UpdateOverlay(() =>
            {
                if (projectStatuses.ContainsKey(projectPath))
                {
                    projectStatuses[projectPath] = Models.ProjectRunStatus.Running;
                }
            });

            void OnProjectProgress(string projectPath, IReadOnlyList<TestSuite> liveSuites) =>
                UpdateOverlay(() => liveByProject[projectPath] = liveSuites, throttle: true);

            void OnProjectCompleted(Models.ProjectTestResult result) => UpdateOverlay(() =>
            {
                _state.CompletedTestProjects++;

                var projectStatus = result.Crashed
                    ? Models.ProjectRunStatus.Crashed
                    : result.RunnerError is not null && result.Suites.Count == 0
                        ? Models.ProjectRunStatus.Failed
                        : Models.ProjectRunStatus.Completed;

                if (projectStatuses.ContainsKey(result.ProjectPath))
                {
                    projectStatuses[result.ProjectPath] = projectStatus;
                }

                projectOutcomes[result.ProjectPath] = new TestRunProjectOutcome(
                    result.ProjectPath, projectStatus, result.Suites.Sum(s => s.Tests.Count));

                if (result.Suites.Count > 0)
                {
                    liveByProject[result.ProjectPath] = result.Suites;

                    // The project's final results are the authority on what ran: drop its discovery
                    // placeholders, whose listed names may not match the final identities.
                    if (!result.Crashed)
                    {
                        _ = settledProjects.Add(result.ProjectPath);
                        _ = discoveredByProject.Remove(result.ProjectPath);
                    }
                }
            });

            // Best-effort discovery, streamed, so a project's test list shows before its results arrive.
            // Unfiltered runs only discover projects without known results (those are already seeded);
            // filtered runs discover every target with the filter, to tell which known tests will run.
            using var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var discoveryTask = Task.CompletedTask;
            if (testTargets is { Count: > 0 })
            {
                var knownSuiteNames = suitesToSeed.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
                var toDiscover = filtered
                    ? [.. testTargets]
                    : testTargets.Where(p => !knownSuiteNames.Contains(TestProjectNames.SuiteName(p))).ToList();
                if (toDiscover.Count > 0)
                {
                    discoveryTask = _testRunner.DiscoverTestsAsync(
                        toDiscover,
                        effectiveFilter,
                        (projectPath, tests) => UpdateOverlay(() =>
                        {
                            if (!settledProjects.Contains(projectPath))
                            {
                                discoveredByProject[projectPath] = tests;
                            }
                        }, throttle: true),
                        discoveryCts.Token);
                }
            }

            async Task EndTestPhaseAsync()
            {
                lock (liveResultsLock)
                {
                    testPhaseOver = true;
                }

                await discoveryCts.CancelAsync().ConfigureAwait(false);
                try
                {
                    await discoveryTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log?.Write("Orchestrator", $"Test discovery failed: {ex.Message}");
                }
            }

            // Generate a run ID before running tests (per plan: CreateRunId before ProcessCoverageAsync)
            var runId = _coverageEnabled && _coverageStore is not null
                ? _coverageStore.CreateRunId()
                : 0;

            IReadOnlyList<TestSuite> newSuites;
            IReadOnlyList<string> coverageReportPaths;
            IReadOnlyList<ProjectTestResult> projectResults;
            string? runnerError;
            try
            {
                var result = await _testRunner.RunTestsAsync(
                    solutionPath,
                    testTargets,
                    effectiveFilter,
                    _coverageEnabled,
                    new TestRunCallbacks
                    {
                        OnProjectStarted = OnProjectStarted,
                        OnProjectProgress = OnProjectProgress,
                        OnProjectCompleted = OnProjectCompleted,
                    },
                    ct).ConfigureAwait(false);
                newSuites = result.Suites.Select(s => s with { RunId = run.RunId }).ToList();
                coverageReportPaths = result.CoverageReportPaths;
                projectResults = result.Projects;
                runnerError = result.RunnerError;
                _state.LastTestRunnerError = result.RunnerError;

                var totalTests = newSuites.SelectMany(s => s.Tests).Count();
                log?.Write("Orchestrator",
                    $"TestRun complete: {totalTests} test(s) in {newSuites.Count} suite(s)" +
                    (result.RunnerError is not null ? $" | runnerError={result.RunnerError}" : ""));
            }
            catch (OperationCanceledException)
            {
                _state.Phase = PistonPhase.Watching;
                _state.NotifyChanged();
                return Finish(TestRunStatus.Cancelled, "Cancelled during test execution.");
            }
            finally
            {
                await EndTestPhaseAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
            {
                _state.Phase = PistonPhase.Watching;
                _state.NotifyChanged();
                return Finish(TestRunStatus.Cancelled, "Cancelled after test execution; results discarded.");
            }

            // ── Coverage processing ────────────────────────────────────────────
            if (_coverageEnabled && _coverageStore is not null && _coverageProcessor is not null
                && coverageReportPaths.Count > 0 && newSuites.Count > 0)
            {
                var testFqns = newSuites
                    .SelectMany(s => s.Tests)
                    .Select(t => t.FullyQualifiedName)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                try
                {
                    // Only an unfiltered run of every test project, with none crashing, measures the whole solution.
                    var isFullCoverageRun = impactResult.IsFullRun
                        && string.IsNullOrWhiteSpace(effectiveFilter)
                        && runnerError is null;
                    var impact = new CoverageImpactOptions(
                        RunImpactUnits(projectResults, solutionPath, coverageReportPaths, testFqns, effectiveFilter),
                        ImpactGranularity.Run,
                        (run.StartedAt ?? DateTimeOffset.UtcNow).UtcDateTime);
                    await ProcessCoverageAsync(runId, coverageReportPaths, testFqns, isFullCoverageRun, impact)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Coverage processing failure is non-fatal
                }
                finally
                {
                    // Clean up temp results directories after coverage has been processed
                    CleanUpCoverageResultsDirs(coverageReportPaths);
                }
            }
            else if (coverageReportPaths.Count > 0)
            {
                CleanUpCoverageResultsDirs(coverageReportPaths);
            }

            // Merge selective results with preserved results from untouched test projects
            _state.TestSuites = impactResult.IsFullRun
                ? newSuites
                : MergeTestSuites(_state.TestSuites, newSuites);

            _state.InProgressSuites = [];
            _state.LastRunTime = DateTimeOffset.UtcNow;
            _state.LastTestDuration = DateTimeOffset.UtcNow - testStart;

            // Emit test run completion activity event
            EmitTestRunCompleted(solutionPath, newSuites, _state.LastTestDuration.Value);

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

            List<TestRunProjectOutcome> outcomes;
            lock (liveResultsLock)
            {
                outcomes = projectOutcomes.Values.ToList();
            }

            var duplicates = newSuites
                .SelectMany(s => s.Tests)
                .GroupBy(t => t.ResultKey, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .Order(StringComparer.Ordinal)
                .ToList();

            run = run with
            {
                Suites = newSuites,
                Projects = outcomes,
                DuplicateTestIds = duplicates,
                RunnerError = runnerError,
            };

            var incompleteReason = DescribeIncomplete(
                outcomes, testTargets is null, newSuites, runnerError,
                filtered: !string.IsNullOrWhiteSpace(effectiveFilter));
            var finished = Finish(
                incompleteReason is null ? TestRunStatus.Completed : TestRunStatus.Incomplete,
                incompleteReason,
                computeAfter: true);

            _state.Phase = PistonPhase.Watching;
            _state.NotifyChanged();

            ScheduleGraphRebuildIfNeeded(impactResult);
            return finished;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer run — reset phase cleanly
            _state.Phase = PistonPhase.Watching;
            _state.NotifyChanged();
            return Finish(TestRunStatus.Cancelled, "Superseded by a newer run.");
        }
        catch (Exception ex)
        {
            _ = Finish(TestRunStatus.Faulted, $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            try
            {
                lease.Dispose();
            }
            finally
            {
                _ = _runLock.Release();
            }
        }
    }

    // ── Run provenance ────────────────────────────────────────────────────────

    private sealed record BuildBaseline(
        string SolutionPath,
        SourceFingerprint Fingerprint,
        IReadOnlySet<string>? Targets,
        long ChangeGeneration);

    // True when the last successful build covered the targets, nothing in the fingerprint changed
    // since and no watcher change was seen, and the known test outputs still exist.
    private bool CanReuseLastBuild(string solutionPath, SourceFingerprint? fingerprint, IReadOnlyList<string>? targets)
    {
        if (_buildBaseline is not { } baseline
            || fingerprint is null
            || baseline.Fingerprint != fingerprint
            || Interlocked.Read(ref _fileChangeGeneration) != baseline.ChangeGeneration
            || !string.Equals(baseline.SolutionPath, Path.GetFullPath(solutionPath), StringComparison.Ordinal))
        {
            return false;
        }

        if (targets is null)
        {
            return baseline.Targets is null;
        }

        return targets.All(target =>
            (baseline.Targets is null || baseline.Targets.Contains(Path.GetFullPath(target)))
            && (_impactAnalyzer.GetMtpOutputPath(target) is not { } output || File.Exists(output)));
    }

    // Captured per run so a later verification never binds a result to inputs it cannot see.
    private bool IsInputCoverageVerified(string solutionPath)
    {
        try
        {
            return _impactAnalyzer.GetVerificationCoverageIssue(solutionPath) is null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Instance?.Write("Orchestrator",
                $"Build-input coverage evaluation failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private TestRunRecord CreateRunRecord(TestRunTrigger trigger) => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        SolutionPath = _state.SolutionPath,
        EngineInstanceId = _engineInstanceId,
        Sequence = Interlocked.Increment(ref _runSequence),
        Trigger = trigger,
        RequestedAt = DateTimeOffset.UtcNow,
    };

    private void PublishActiveRun(TestRunRecord run)
    {
        lock (_runRecordsLock)
        {
            _state.CurrentRun = run;
        }

        _state.NotifyChanged();
    }

    /// <summary>
    /// Finalises a run record: captures the post-run fingerprint and decides freshness.
    /// A run is <see cref="ResultFreshness.Current"/> only when both fingerprints are available and
    /// equal and the file watcher reported no change while it executed.
    /// </summary>
    private TestRunRecord CompleteRun(
        TestRunRecord run,
        TestRunStatus status,
        string? reason,
        string? solutionDir = null,
        long changeGenerationAtStart = 0,
        bool computeAfter = false)
    {
        var started = run.StartedAt is not null;
        var changeObserved = started && Interlocked.Read(ref _fileChangeGeneration) != changeGenerationAtStart;
        var after = computeAfter && solutionDir is not null ? SourceFingerprinter.TryCompute(solutionDir) : null;

        (var freshness, var freshnessReason) = (run.SourceBefore, after) switch
        {
            _ when !started => (ResultFreshness.Unknown, "Run did not start."),
            ({ } before, { } afterFp) when before != afterFp =>
                (ResultFreshness.Stale, "Source fingerprint changed during the run."),
            _ when changeObserved =>
                (ResultFreshness.Stale, "File watcher reported a source change during the run."),
            (null, _) => (ResultFreshness.Unknown, "Pre-run source fingerprint unavailable."),
            (_, null) when computeAfter => (ResultFreshness.Unknown, "Post-run source fingerprint unavailable."),
            (_, null) => (ResultFreshness.Unknown, "Run did not finish; post-run fingerprint not captured."),
            _ => (ResultFreshness.Current, null),
        };

        var finished = run with
        {
            Status = status,
            StatusReason = reason,
            CompletedAt = DateTimeOffset.UtcNow,
            SourceAfter = after,
            SourceChangeObserved = changeObserved,
            Freshness = freshness,
            FreshnessReason = freshnessReason,
        };

        lock (_runRecordsLock)
        {
            if (_state.CurrentRun?.RunId == finished.RunId)
            {
                _state.CurrentRun = null;
            }

            if (_state.LastRun is null || _state.LastRun.Sequence < finished.Sequence)
            {
                _state.LastRun = finished;
            }

            var recent = new List<TestRunRecord>(_state.RecentRuns.Count + 1) { finished };
            recent.AddRange(_state.RecentRuns.Take(PistonState.MaxRecentRuns - 1));
            _state.RecentRuns = recent;

            if (_runWaiters.Remove(finished.RunId, out var waiter))
            {
                _ = waiter.TrySetResult(finished);
            }
        }

        _state.NotifyChanged();
        return finished;
    }

    private static string? DescribeIncomplete(
        IReadOnlyList<TestRunProjectOutcome> outcomes,
        bool solutionTarget,
        IReadOnlyList<TestSuite> suites,
        string? runnerError,
        bool filtered)
    {
        // A filter can legitimately select no tests in a project; filtered runs are never full scope anyway.
        var problems = outcomes
            .Where(o => o.Status != Models.ProjectRunStatus.Completed || (o.TestCount == 0 && !filtered))
            .Select(o => $"{Path.GetFileNameWithoutExtension(o.ProjectPath)}: " +
                (o.Status == Models.ProjectRunStatus.Completed ? "no test results" : o.Status.ToString()))
            .ToList();

        if (solutionTarget && suites.Count == 0 && !filtered)
        {
            problems.Add("solution-level run produced no test results");
        }

        if (runnerError is not null && problems.Count == 0)
        {
            problems.Add("runner reported an error");
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    private string? SafeSdkVersion(string workingDirectory)
    {
        try { return SdkVersionProvider?.Invoke(workingDirectory); }
        catch { return null; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EmitTestRunCompleted(string solutionPath, IReadOnlyList<TestSuite> suites, TimeSpan duration)
    {
        var allTests = suites.SelectMany(s => s.Tests).ToList();
        var passed = allTests.Count(t => t.Status == TestStatus.Passed);
        var failed = allTests.Count(t => t.Status == TestStatus.Failed);
        var skipped = allTests.Count(t => t.Status == TestStatus.Skipped);

        var failures = allTests
            .Where(t => t.Status == TestStatus.Failed)
            .Select(t => new TestFailure(t.ResultKey, t.ErrorMessage))
            .ToList();

        _activitySink.Emit(new TestRunCompletedActivityEvent(
            solutionPath, passed, failed, skipped, duration.TotalMilliseconds, failures));
    }

    /// <summary>
    /// Builds the effective dotnet-test filter expression, combining Tier 3 FQN filter
    /// with the user's optional test filter.
    /// </summary>
    private static string? BuildEffectiveFilter(ImpactAnalysisResult impactResult, string? userFilter)
    {
        string? tier3Filter = null;

        if (impactResult.AffectedTestFqns is { Count: > 0 } fqns)
        {
            // Limit to 100 FQNs to avoid command-line length issues; fall back to Tier 2 if over limit
            if (fqns.Count <= 100)
            {
                tier3Filter = string.Join("|",
                    fqns.Select(f => $"FullyQualifiedName={f}"));
            }
        }

        if (tier3Filter is null)
        {
            return userFilter;
        }

        if (string.IsNullOrWhiteSpace(userFilter))
        {
            return tier3Filter;
        }

        return $"({tier3Filter})&({userFilter})";
    }

    private static void CleanUpCoverageResultsDirs(IReadOnlyList<string> coverageReportPaths)
    {
        var resultsDirs = coverageReportPaths
            .Select(CoverageResultsDirectory)
            .Where(d => d is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dir in resultsDirs)
        {
            try { Directory.Delete(dir!, recursive: true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// The <c>piston-*</c> temp directory a runner wrote <paramref name="reportPath"/> under, or
    /// <see langword="null"/> when it is not in one. Reports are at different depths: coverlet nests each in
    /// a GUID directory, the MTP extension writes them directly in the results directory.
    /// </summary>
    internal static string? CoverageResultsDirectory(string reportPath)
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(reportPath)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (string.Equals(Path.GetDirectoryName(dir), tempRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(dir).StartsWith("piston-", StringComparison.Ordinal))
            {
                return dir;
            }
        }

        return null;
    }

    private void ScheduleGraphRebuildIfNeeded(ImpactAnalysisResult impactResult)
    {
        if (impactResult.RequiresGraphRebuild)
        {
            _impactAnalyzer.InvalidateGraph();
        }
    }

    /// <summary>
    /// Determines whether a test suite is in the set of affected test project paths.
    /// Matches by suite name suffix against project directory/name heuristic.
    /// Falls back to including all suites when uncertain.
    /// </summary>
    private static bool IsSuiteAffected(TestSuite suite, IReadOnlyList<string> testProjectPaths) =>
        // If we have test project paths, check if the suite's source matches any
        testProjectPaths.Any(p =>
        {
            var projectDir = Path.GetDirectoryName(p);
            return projectDir is not null &&
                suite.Name.Contains(Path.GetFileNameWithoutExtension(p), StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>
    /// Merges new suite results from a selective run into the existing full suite list.
    /// Tests are matched by <see cref="TestResult.ResultKey"/> (name and target framework): re-run tests
    /// replace their previous results, while tests that were not re-run are preserved. A result without a
    /// known framework matches the test under any framework. Suite names are not used for matching
    /// because they are not guaranteed to identify a project (e.g. TRX run names).
    /// </summary>
    internal static IReadOnlyList<TestSuite> MergeTestSuites(
        IReadOnlyList<TestSuite> existing,
        IReadOnlyList<TestSuite> newResults)
    {
        if (newResults.Count == 0)
        {
            return existing;
        }

        if (existing.Count == 0)
        {
            return newResults;
        }

        var rerunTests = newResults.SelectMany(s => s.Tests).ToList();
        var rerunKeys = rerunTests.Select(t => t.ResultKey).ToHashSet(StringComparer.Ordinal);
        var rerunNames = rerunTests.Select(t => t.FullyQualifiedName).ToHashSet(StringComparer.Ordinal);
        var rerunAnyFramework = rerunTests
            .Where(t => t.TargetFramework is null)
            .Select(t => t.FullyQualifiedName)
            .ToHashSet(StringComparer.Ordinal);

        bool Rerun(TestResult t) =>
            rerunKeys.Contains(t.ResultKey)
            || rerunAnyFramework.Contains(t.FullyQualifiedName)
            || (t.TargetFramework is null && rerunNames.Contains(t.FullyQualifiedName));

        var merged = existing
            .Select(s => s with { Tests = s.Tests.Where(t => !Rerun(t)).ToList() })
            .Where(s => s.Tests.Count > 0)
            .ToList();

        merged.AddRange(newResults);
        return merged;
    }

    private static readonly TimeSpan ProgressNotifyInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Builds the live test overlay shown while tests run. Starts from the last known results for the
    /// targeted suites, adds discovered tests, then applies live results per test, so only tests the
    /// runner reports as started are <see cref="TestStatus.Running"/>. Tests without a live result are
    /// <see cref="TestStatus.NotRun"/> (queued) — except in a <paramref name="filtered"/> run, where a known
    /// test that discovery did not select keeps its last status because it is not part of the run.
    /// </summary>
    internal static IReadOnlyList<TestSuite> ComposeTestOverlay(
        IReadOnlyList<TestSuite> lastSuites,
        IReadOnlyDictionary<string, IReadOnlyList<TestResult>> discoveredByProject,
        IReadOnlyDictionary<string, IReadOnlyList<TestSuite>> liveByProject,
        bool filtered) =>
        ComposeTestOverlay(lastSuites, discoveredByProject, liveByProject, filtered, out _, out _);

    /// <inheritdoc cref="ComposeTestOverlay(IReadOnlyList{TestSuite}, IReadOnlyDictionary{string, IReadOnlyList{TestResult}}, IReadOnlyDictionary{string, IReadOnlyList{TestSuite}}, bool)"/>
    /// <param name="expectedTests">The number of overlay tests that are part of this run.</param>
    /// <param name="completedTests">How many of those have finished (passed, failed or skipped).</param>
    internal static IReadOnlyList<TestSuite> ComposeTestOverlay(
        IReadOnlyList<TestSuite> lastSuites,
        IReadOnlyDictionary<string, IReadOnlyList<TestResult>> discoveredByProject,
        IReadOnlyDictionary<string, IReadOnlyList<TestSuite>> liveByProject,
        bool filtered,
        out int expectedTests,
        out int completedTests)
    {
        // Duplicate identities are tolerated here (last wins) and reported on the run record. Live results
        // are keyed by name and framework; one without a framework (VSTest console and collector progress)
        // stands for the test under every framework until the final results tell them apart.
        var liveByKey = new Dictionary<string, TestResult>(StringComparer.Ordinal);
        foreach (var test in liveByProject.Values.SelectMany(suites => suites).SelectMany(s => s.Tests))
        {
            liveByKey[test.ResultKey] = test;
        }

        var selected = filtered ? new HashSet<string>(StringComparer.Ordinal) : null;
        var placeholders = new List<(string SuiteName, TestResult Test)>();
        foreach (var (projectPath, tests) in discoveredByProject)
        {
            var suiteName = TestProjectNames.SuiteName(projectPath);
            var matcher = new DiscoveredNameMatcher(
                lastSuites.Where(s => s.Name == suiteName)
                    .Concat(liveByProject.GetValueOrDefault(projectPath) ?? [])
                    .SelectMany(s => s.Tests)
                    .Select(t => t.FullyQualifiedName));
            foreach (var test in tests)
            {
                var matches = matcher.Match(test.FullyQualifiedName);
                if (matches.Count == 0)
                {
                    placeholders.Add((suiteName, test));
                    _ = selected?.Add(test.FullyQualifiedName);
                }
                else
                {
                    selected?.UnionWith(matches);
                }
            }
        }

        var expected = 0;
        var completed = 0;

        TestResult Resolve(TestResult test)
        {
            var resolved = liveByKey.TryGetValue(test.ResultKey, out var live)
                || (test.TargetFramework is not null && liveByKey.TryGetValue(test.FullyQualifiedName, out live))
                ? live with { TargetFramework = test.TargetFramework ?? live.TargetFramework }
                : selected is not null && !selected.Contains(test.FullyQualifiedName)
                    ? null
                    : test with { Status = TestStatus.NotRun };
            if (resolved is null)
            {
                // A known test outside this filtered run: shown with its last status, but not counted.
                return test;
            }

            expected++;
            if (resolved.Status is TestStatus.Passed or TestStatus.Failed or TestStatus.Skipped)
            {
                completed++;
            }

            return resolved;
        }

        var suites = new List<(string Name, DateTimeOffset Timestamp, TimeSpan Duration, List<TestResult> Tests)>();
        var suiteIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var placedNames = new HashSet<string>(StringComparer.Ordinal);

        void Add(string suiteName, TestResult test)
        {
            // A result without a framework is already shown when the test is, under any framework.
            if (test.TargetFramework is null ? placedNames.Contains(test.FullyQualifiedName) : !placed.Add(test.ResultKey))
            {
                return;
            }

            _ = placed.Add(test.ResultKey);
            _ = placedNames.Add(test.FullyQualifiedName);

            if (!suiteIndex.TryGetValue(suiteName, out var index))
            {
                index = suites.Count;
                suiteIndex[suiteName] = index;
                suites.Add((suiteName, DateTimeOffset.UtcNow, TimeSpan.Zero, []));
            }

            suites[index].Tests.Add(Resolve(test));
        }

        foreach (var suite in lastSuites)
        {
            if (!suiteIndex.ContainsKey(suite.Name))
            {
                suiteIndex[suite.Name] = suites.Count;
                suites.Add((suite.Name, suite.Timestamp, suite.TotalDuration, []));
            }

            foreach (var test in suite.Tests)
            {
                Add(suite.Name, test);
            }
        }

        // Discovery does not say which framework a test is listed for; once results for a test arrive
        // per framework, they stand in for its placeholder.
        var namesWithFrameworkResults = liveByKey.Values
            .Where(t => t.TargetFramework is not null)
            .Select(t => t.FullyQualifiedName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (suiteName, test) in placeholders)
        {
            if (!namesWithFrameworkResults.Contains(test.FullyQualifiedName))
            {
                Add(suiteName, test);
            }
        }

        foreach (var (projectPath, projectSuites) in liveByProject)
        {
            foreach (var suite in projectSuites)
            {
                var suiteName = TestProjectNames.IsProjectFile(projectPath)
                    ? TestProjectNames.SuiteName(projectPath)
                    : suite.Name;
                foreach (var test in suite.Tests)
                {
                    Add(suiteName, test);
                }
            }
        }

        expectedTests = expected;
        completedTests = completed;
        return suites
            .Where(s => s.Tests.Count > 0)
            .Select(s => new TestSuite(s.Name, s.Tests, s.Timestamp, s.Duration))
            .ToList();
    }

    /// <summary>
    /// Matches the names <c>--list-tests</c> prints against one project's known test identities. VSTest
    /// lists display names, which may lack the class (MSTest and NUnit print <c>Method</c> for
    /// <c>Ns.Class.Method</c>), so a name that is not itself a known identity stands for the known tests
    /// of the same project whose identity ends with <c>.name</c>. Matching is per project, never global.
    /// </summary>
    private sealed class DiscoveredNameMatcher(IEnumerable<string> knownIdentities)
    {
        private readonly HashSet<string> _known = knownIdentities.ToHashSet(StringComparer.Ordinal);
        private Dictionary<string, List<string>>? _bySuffix;

        public IReadOnlyCollection<string> Match(string listedName)
        {
            if (_known.Contains(listedName))
            {
                return [listedName];
            }

            _bySuffix ??= IndexBySuffix(_known);
            return _bySuffix.TryGetValue(listedName, out var matches) ? matches : [];
        }

        private static Dictionary<string, List<string>> IndexBySuffix(HashSet<string> identities)
        {
            var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var identity in identities)
            {
                for (var dot = identity.IndexOf('.'); dot >= 0; dot = identity.IndexOf('.', dot + 1))
                {
                    var suffix = identity[(dot + 1)..];
                    if (!index.TryGetValue(suffix, out var list))
                    {
                        index[suffix] = list = [];
                    }

                    list.Add(identity);
                }
            }

            return index;
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            _stopping = true;
            _cts?.Cancel();
            _disposeTask = DisposeCoreAsync(_activePipelines == 0 ? Task.CompletedTask : _pipelinesDrained!.Task);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task drained)
    {
        _fileWatcher.FileChanged -= OnFileChanged;
        _fileWatcher.Dispose();
        await drained.ConfigureAwait(false);
        if (_graphInitTask is { } graphInit)
        {
            await graphInit.ConfigureAwait(false);
        }

        _runLock.Dispose();
        _resetLock.Dispose();
        _coverageLock.Dispose();
    }
}
