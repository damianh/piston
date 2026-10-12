// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Execution;
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
    private readonly SemaphoreSlim _coverageLock = new(1, 1);
    private readonly string _engineInstanceId = Guid.NewGuid().ToString("N");
    private readonly object _runRecordsLock = new();
    private readonly Dictionary<string, TaskCompletionSource<TestRunRecord>> _runWaiters = new(StringComparer.Ordinal);
    private long _runSequence;
    private long _fileChangeGeneration;

    /// <summary>Maximum runs reused, awaited or requested by one <see cref="VerifyAsync"/> call.</summary>
    internal int MaxVerificationAttempts { get; set; } = 3;

    /// <summary>Longest time <see cref="VerifyAsync"/> waits for debounced watcher changes to be raised.</summary>
    internal TimeSpan VerificationSettleTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Resolves the .NET SDK version recorded in run context; null when unknown.</summary>
    internal Func<string?>? SdkVersionProvider { get; set; }

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
                var evaluation = VerificationPolicy.Evaluate(run, after, requiredTestProjects);
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

    internal async Task ProcessCoverageAsync(
        long runId, IReadOnlyList<string> reportPaths, IReadOnlyList<string> testFqns)
    {
        var processor = _coverageProcessor
            ?? throw new InvalidOperationException("Coverage processing is not enabled.");
        var store = _coverageStore
            ?? throw new InvalidOperationException("Coverage storage is not enabled.");
        await _coverageLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await processor.ProcessCoverageAsync(runId, reportPaths, testFqns, store)
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

    private Task<TestRunRecord> TriggerRunAsync(string solutionPath, FileChangeBatch? batch, TestRunTrigger trigger)
    {
        CancellationTokenSource cts;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return Task.FromResult(CompleteRun(CreateRunRecord(trigger), TestRunStatus.Cancelled, "Engine is shutting down."));
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
        return RunTrackedAsync(solutionPath, batch, trigger, cts);
    }

    private async Task<TestRunRecord> RunTrackedAsync(
        string solutionPath, FileChangeBatch? batch, TestRunTrigger trigger, CancellationTokenSource cts)
    {
        try
        {
            return await RunPipelineAsync(solutionPath, batch, trigger, cts).ConfigureAwait(false);
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
        string solutionPath, FileChangeBatch? batch, TestRunTrigger trigger, CancellationTokenSource newCts)
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

        TestRunRecord Finish(TestRunStatus status, string? reason, bool computeAfter = true) =>
            CompleteRun(run, status, reason, solutionDir, changeGenerationAtStart, computeAfter);

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
            log?.Write("Orchestrator", batch is not null
                ? $"TriggerRun: {batch.Changes.Count} file change(s)"
                : "TriggerRun: full run (no batch)");

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
            //   - If Tier 3 produced FQNs, build a dotnet-test filter expression
            //   - If the user also has a TestFilter, AND the two together
            var userFilter = _state.TestFilter;
            var effectiveFilter = BuildEffectiveFilter(impactResult, userFilter);

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
                    SdkVersion: SafeSdkVersion(),
                    Runtime: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    OperatingSystem: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    WorkingDirectory: Environment.CurrentDirectory,
                    TestWorkingDirectories: (testTargets ?? [Path.GetFullPath(solutionPath)])
                        .ToDictionary(
                            path => path,
                            path => _impactAnalyzer.GetMtpOutputPath(path) is not null ? solutionDir : Environment.CurrentDirectory,
                            StringComparer.Ordinal)),
            };
            PublishActiveRun(run);

            if (ct.IsCancellationRequested)
            {
                _state.Phase = PistonPhase.Watching;
                _state.NotifyChanged();
                return Finish(TestRunStatus.Cancelled, "Cancelled after impact analysis.");
            }

            // ── Build ─────────────────────────────────────────────────────────
            _state.Phase = PistonPhase.Building;
            _state.NotifyChanged();

            BuildResult buildResult;
            try
            {
                buildResult = await _buildService.BuildAsync(solutionPath, buildTargets, ct).ConfigureAwait(false);
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

            _activitySink.Emit(new BuildCompletedActivityEvent(
                solutionPath,
                buildResult.Status == BuildStatus.Succeeded,
                buildResult.Duration.TotalMilliseconds,
                buildResult.Errors));

            if (ct.IsCancellationRequested)
            {
                _state.Phase = PistonPhase.Watching;
                _state.NotifyChanged();
                return Finish(TestRunStatus.Cancelled, "Cancelled after build.");
            }

            if (buildResult.Status == BuildStatus.Failed)
            {
                _state.Phase = PistonPhase.Error;
                _state.NotifyChanged();
                ScheduleGraphRebuildIfNeeded(impactResult);
                return Finish(TestRunStatus.BuildFailed,
                    $"Build failed with {buildResult.Errors.Count} error(s).", computeAfter: true);
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
                _state.CoverageImpactDetail = $"Tier 3: {fqns.Count} test(s) (coverage)";
            }
            else
            {
                _state.CoverageImpactDetail = null;
            }

            // Seed InProgressSuites — for selective runs, only seed the affected suites
            var suitesToSeed = testTargets is not null
                ? _state.TestSuites.Where(s => IsSuiteAffected(s, testTargets)).ToList()
                : _state.TestSuites;

            _state.InProgressSuites = SeedAsRunning(suitesToSeed);
            _state.TotalExpectedTests = suitesToSeed.SelectMany(s => s.Tests).Count();
            _state.CompletedTests = 0;

            // Per-project progress tracking
            _state.TotalTestProjects = testTargets?.Count ?? 0;
            _state.CompletedTestProjects = 0;
            _state.ProjectStatuses = testTargets is not null
                ? testTargets.ToDictionary(p => p, _ => Models.ProjectRunStatus.Pending)
                : new Dictionary<string, Models.ProjectRunStatus>();

            // Run-local project outcomes (independent of the UI-facing ProjectStatuses).
            var projectOutcomes = new Dictionary<string, TestRunProjectOutcome>(StringComparer.Ordinal);
            foreach (var project in testTargets ?? [])
            {
                projectOutcomes[project] = new TestRunProjectOutcome(project, Models.ProjectRunStatus.Pending, 0);
            }

            _state.NotifyChanged();

            var testStart = DateTimeOffset.UtcNow;
            var liveResultsLock = new object();

            void OnProgress(IReadOnlyList<TestSuite> liveSuites)
            {
                lock (liveResultsLock)
                {
                    _state.InProgressSuites = MergeProgress(suitesToSeed, liveSuites);
                    _state.CompletedTests = _state.InProgressSuites
                        .SelectMany(s => s.Tests)
                        .Count(t => t.Status != TestStatus.Running);
                }
                _state.NotifyChanged();
            }

            void OnProjectCompleted(Models.ProjectTestResult result)
            {
                lock (liveResultsLock)
                {
                    _state.CompletedTestProjects++;

                    var projectStatus = result.Crashed
                        ? Models.ProjectRunStatus.Crashed
                        : result.RunnerError is not null && result.Suites.Count == 0
                            ? Models.ProjectRunStatus.Failed
                            : Models.ProjectRunStatus.Completed;

                    if (_state.ProjectStatuses is Dictionary<string, Models.ProjectRunStatus> mutableStatuses)
                    {
                        mutableStatuses[result.ProjectPath] = projectStatus;
                    }

                    projectOutcomes[result.ProjectPath] = new TestRunProjectOutcome(
                        result.ProjectPath, projectStatus, result.Suites.Sum(s => s.Tests.Count));

                    // Merge the completed project's suites into InProgressSuites
                    if (result.Suites.Count > 0)
                    {
                        _state.InProgressSuites = MergeProgress(suitesToSeed, result.Suites);
                        _state.CompletedTests = _state.InProgressSuites
                            .SelectMany(s => s.Tests)
                            .Count(t => t.Status != TestStatus.Running);
                    }
                }
                _state.NotifyChanged();
            }

            // Generate a run ID before running tests (per plan: CreateRunId before ProcessCoverageAsync)
            var runId = _coverageEnabled && _coverageStore is not null
                ? _coverageStore.CreateRunId()
                : 0;

            IReadOnlyList<TestSuite> newSuites;
            IReadOnlyList<string> coverageReportPaths;
            string? runnerError;
            try
            {
                var result = await _testRunner.RunTestsAsync(
                    solutionPath,
                    testTargets,
                    effectiveFilter,
                    _coverageEnabled,
                    OnProgress,
                    OnProjectCompleted,
                    ct).ConfigureAwait(false);
                newSuites = result.Suites.Select(s => s with { RunId = run.RunId }).ToList();
                coverageReportPaths = result.CoverageReportPaths;
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
                    await ProcessCoverageAsync(runId, coverageReportPaths, testFqns).ConfigureAwait(false);
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
                .GroupBy(t => t.FullyQualifiedName, StringComparer.Ordinal)
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

    private TestRunRecord CreateRunRecord(TestRunTrigger trigger) => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
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

    private string? SafeSdkVersion()
    {
        try { return SdkVersionProvider?.Invoke(); }
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
            .Select(t => new TestFailure(t.FullyQualifiedName, t.ErrorMessage))
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
        // Each report is in a subdirectory under the results temp dir.
        // Walk up two levels: coverage.cobertura.xml → guid subdir → results dir
        var resultsDirs = coverageReportPaths
            .Select(p => Path.GetDirectoryName(Path.GetDirectoryName(p)))
            .Where(d => d is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dir in resultsDirs)
        {
            try { Directory.Delete(dir!, recursive: true); } catch { /* ignore */ }
        }
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
    /// Tests are matched by fully-qualified name: re-run tests replace their previous results,
    /// while tests that were not re-run are preserved. Suite names are not used for matching
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

        var rerun = newResults
            .SelectMany(s => s.Tests)
            .Select(t => t.FullyQualifiedName)
            .ToHashSet(StringComparer.Ordinal);

        var merged = existing
            .Select(s => s with { Tests = s.Tests.Where(t => !rerun.Contains(t.FullyQualifiedName)).ToList() })
            .Where(s => s.Tests.Count > 0)
            .ToList();

        merged.AddRange(newResults);
        return merged;
    }

    /// <summary>
    /// Returns a copy of <paramref name="suites"/> with every test status set to Running.
    /// Used to seed the live overlay at the start of a test phase.
    /// </summary>
    private static IReadOnlyList<TestSuite> SeedAsRunning(IEnumerable<TestSuite> suites) => suites
            .Select(s => s with
            {
                Tests = s.Tests
                    .Select(t => t with { Status = TestStatus.Running })
                    .ToList()
            })
            .ToList();

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

        if (liveSuites.Count == 0)
        {
            return SeedAsRunning(lastSuiteList);
        }

        // Build a lookup from the flat live list
        // Duplicate identities are tolerated here (last wins) and reported on the run record.
        var liveByFqn = new Dictionary<string, TestStatus>(StringComparer.Ordinal);
        foreach (var test in liveSuites.SelectMany(s => s.Tests))
        {
            liveByFqn[test.FullyQualifiedName] = test.Status;
        }

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
        _coverageLock.Dispose();
    }
}
