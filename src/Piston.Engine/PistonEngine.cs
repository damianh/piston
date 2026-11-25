// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Execution;
using Piston.Engine.History;
using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;

namespace Piston.Engine;

public sealed class PistonEngine : IEngine, IAsyncDisposable
{
    private readonly PistonState _state;
    private readonly PistonOrchestrator _orchestrator;
    private readonly ICoverageStore? _coverageStore;
    private readonly ITestProcessPool _pool;
    private readonly DiagnosticLog _diagnosticLog;
    private readonly IActivityEventSink _activitySink;
    private readonly string _solutionDirectory;
    private readonly string _solutionPath;
    private readonly TestArtifactStore _artifactStore;
    private readonly ExecutionCoordinationDirectory _coordinationDirectory;
    private readonly string? _ownerHost;
    private readonly string? _ownerDashboardUrl;
    private readonly bool _historyEnabled;
    private readonly MtpWarmHostPool? _warmHosts;
    private readonly string? _historyDirectory;
    private readonly bool _persistResults;
    private readonly bool _requireTrust;
    private readonly bool _trustOnStart;
    private readonly RepositoryTrustStore? _repositoryTrustStore;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private CloneIdentity? _cloneIdentity;
    private WorktreeOwnership? _ownership;
    private RunHistoryStore? _history;
    private SavedResultsStore? _savedResults;
    private volatile bool _orchestratorStarted;
    private int _disposeRequested;
    private bool _disposed;

    public PistonEngine(PistonOptions options)
        : this(options, NullActivityEventSink.Instance, serviceOverrides: null)
    {
    }

    public PistonEngine(PistonOptions options, IActivityEventSink activitySink)
        : this(options, activitySink, serviceOverrides: null)
    {
    }

    internal PistonEngine(PistonOptions options, IActivityEventSink activitySink, EngineServiceOverrides? serviceOverrides)
    {
        _activitySink = activitySink;
        _ownerHost = options.OwnerHost;
        _ownerDashboardUrl = options.OwnerDashboardUrl;
        _historyEnabled = options.HistoryEnabled;
        _historyDirectory = options.HistoryDirectory;
        _persistResults = options.PersistResults;
        _requireTrust = options.RequireTrust;
        _trustOnStart = options.TrustOnStart;
        if (_requireTrust && options.TrustOnStart)
        {
            throw new ArgumentException("TrustOnStart cannot be combined with RequireTrust.", nameof(options));
        }
        _repositoryTrustStore = _requireTrust || options.TrustOnStart
            ? new RepositoryTrustStore(options.RepositoryTrustStorePath ?? RepositoryTrustStore.ResolveDefaultPath())
            : null;

        // Register MSBuild before any Microsoft.Build.* types are loaded.
        // This must happen before MsBuildSolutionGraph (or any class referencing MSBuild) is JIT-compiled.
        MsBuildLocatorGuard.EnsureRegistered();

        // Initialize diagnostic log early so all components can use it; the file is opened once ownership is held.
        _solutionPath = PathNormalizer.GetFullPath(options.SolutionPath);
        var solutionDir = Path.GetDirectoryName(_solutionPath)
            ?? throw new ArgumentException("Cannot resolve solution directory.", nameof(options));
        _solutionDirectory = solutionDir;
        _diagnosticLog = DiagnosticLog.Initialize(solutionDir);
        _artifactStore = new TestArtifactStore(solutionDir, message => _diagnosticLog.Write("Artifacts", message));
        _diagnosticLog.Write("Engine", $"SolutionPath: {_solutionPath}");
        _diagnosticLog.Write("Engine", $"TestExecutionMode: {options.TestExecutionMode}");
        _diagnosticLog.Write("Engine", $"CoverageEnabled: {options.CoverageEnabled}");
        _diagnosticLog.Write("Engine", $"ProcessPoolSize: {options.ProcessPoolSize}");
        _diagnosticLog.Write("Engine", $"TestFilter: {options.TestFilter ?? "(none)"}");

        _state = new PistonState
        {
            TestFilter = options.TestFilter,
            CoverageEnabled = options.CoverageEnabled,
        };

        var fileWatcher = new FileWatcherService(options.DebounceInterval);
        var trxParser = new TrxResultParser();

        ICoverageProcessor? coverageProcessor = null;

        if (options.CoverageEnabled)
        {
            var coberturaParser = new CoberturaParser();
            var coverageStore = serviceOverrides?.CoverageStore ?? new SqliteCoverageStore();
            _coverageStore = coverageStore;
            coverageProcessor = new CoverageProcessor(coberturaParser);
        }

        var impactAnalyzer = new ImpactAnalyzer(
            solutionPath => new MsBuildSolutionGraph(solutionPath),
            _coverageStore,
            project => _state.ProjectCoverage.TryGetValue(project, out var coverage)
                && coverage.Status == CoverageCollectionStatus.Measured,
            () => _state.CoverageError
                ?? (_state.IsPublishingResults || _state.Phase == PistonPhase.Testing
                    ? "test and coverage results are being updated"
                    : null),
            _state.SetCoverageError,
            _state.BeginResultsUpdate,
            _state.EndResultsUpdate);

        var resourceResolver = new TestResourceResolver(
            new TestResourceInference(_solutionPath, options.TestResourceRules, impactAnalyzer.GetTestProjectFacts),
            impactAnalyzer.GetAllTestProjectPaths,
            message => _diagnosticLog.Write("Resources", message));
        var vsTestStrategy = new ProcessTestExecutionStrategy(trxParser);
        _warmHosts = options.WarmTestHosts ? new MtpWarmHostPool() : null;
        var discoveryCache = new MtpDiscoveryCache();
        var buildService = serviceOverrides?.BuildService ?? new BuildService(_warmHosts, discoveryCache);
        bool RequiresResources(string projectPath) => !resourceResolver.Resolve(projectPath).IsEmpty;
        var mtpStrategy = new MtpTestExecutionStrategy(
            projectPath => impactAnalyzer.GetMtpOutputPath(projectPath),
            solutionDir,
            impactAnalyzer.IsMultiTargeted,
            _warmHosts,
            RequiresResources,
            discoveryCache,
            impactAnalyzer.GetMtpTargetOutputs);

        ITestExecutionStrategy strategy = options.TestExecutionMode switch
        {
            TestExecutionMode.InProcess =>
                throw new NotSupportedException("InProcess test execution mode is not yet implemented."),
            _ => new CompositeTestExecutionStrategy(mtpStrategy, vsTestStrategy),
        };

        if (options.TestTimeout is { } testTimeout)
        {
            strategy = new TimeoutTestExecutionStrategy(strategy, testTimeout);
        }

        var effectivePoolSize = options.ProcessPoolSize > 0
            ? options.ProcessPoolSize
            : Math.Max(1, Environment.ProcessorCount / 2);
        _pool = new TestProcessPool(effectivePoolSize, options.ProcessRecycleAfter, strategy);

        _coordinationDirectory = options.ExecutionCoordinationDirectory is { } configuredDirectory
            ? new ExecutionCoordinationDirectory(configuredDirectory)
            : ExecutionCoordinationDirectory.ResolveDefault();

        // Test projects with conflicting exclusive resources wait for each other machine-wide.
        var resourceGate = new MachineResourceCoordinator(
            _coordinationDirectory,
            resourceResolver.Resolve,
            WorktreeIdentity.FromSolutionPath(_solutionPath).RootPath);

        var testRunner = new TestRunnerService(strategy, _pool, resourceGate);

        _orchestrator = new PistonOrchestrator(
            fileWatcher,
            buildService,
            testRunner,
            serviceOverrides?.ImpactAnalyzer ?? impactAnalyzer,
            _state,
            _coverageStore,
            coverageProcessor,
            options.CoverageEnabled,
            activitySink);
        _orchestrator.ArtifactStore = _artifactStore;

        var sdkVersions = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<string?>>(StringComparer.Ordinal);
        _orchestrator.BackfillGranularity = options.ImpactBackfill switch
        {
            ImpactBackfillMode.Class => Impact.ImpactGranularity.Class,
            ImpactBackfillMode.Test => Impact.ImpactGranularity.Test,
            _ => null,
        };
        _orchestrator.BackfillConcurrency = effectivePoolSize;
        _orchestrator.BackfillOutputDirectory = projectPath =>
            mtpStrategy.ServerAppPath(projectPath) is { } appPath ? Path.GetDirectoryName(appPath) : null;
        _orchestrator.StartBackfillDiscovery = discoveryCache.Invalidate;
        _orchestrator.HangThreshold = options.HangThreshold;
        _orchestrator.RetryFailedTests = Math.Clamp(options.RetryFailedTests, 0, PistonOptions.MaxRetryFailedTests);
        _orchestrator.SdkVersionProvider = serviceOverrides?.SdkVersionProvider
            ?? (dir => sdkVersions.GetOrAdd(dir, d => new Lazy<string?>(() => ProbeSdkVersion(d))).Value);
    }

    /// <summary>
    /// Runs <c>dotnet --version</c> in <paramref name="workingDirectory"/>, the solution directory builds start in,
    /// so it reports the SDK the solution's <c>global.json</c> selects.
    /// </summary>
    private static string? ProbeSdkVersion(string workingDirectory)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "--version")
            {
                WorkingDirectory = PathNormalizer.GetSdkWorkingDirectory(workingDirectory),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }
            var version = output.GetAwaiter().GetResult().Trim();
            return process.ExitCode == 0 && version.Length > 0 ? version : null;
        }
        catch
        {
            return null;
        }
    }

    public PistonState State => _state;

    /// <summary>Raised after explicit or external approval starts a previously gated engine.</summary>
    public event Action? RepositoryTrustGranted;

    /// <summary>Canonical clone path displayed while the engine is awaiting trust.</summary>
    public string? RepositoryTrustPath => _cloneIdentity?.RootPath;

    /// <summary>Worktree ownership held by this engine; null before <see cref="StartAsync"/>.</summary>
    internal WorktreeOwnership? Ownership => _ownership;

    public ExecutionOwnerInfo? OwnerInfo => _ownership?.Owner;

    /// <summary>Advertises a bound dashboard while this engine holds worktree ownership.</summary>
    public void PublishDashboardUrl(string? url) => _ownership?.PublishDashboardUrl(url);

    /// <summary>The machine-wide coordination directory used by this engine.</summary>
    public ExecutionCoordinationDirectory ExecutionCoordinationDirectory => _coordinationDirectory;

    /// <summary>
    /// Acquires exclusive ownership of the solution's worktree, then starts watching and runs the initial build/test.
    /// </summary>
    /// <exception cref="ExecutionCoordinationException">
    /// Another live daemon owns the worktree, or machine-wide coordination is unavailable or misconfigured.
    /// </exception>
    public Task StartAsync(string solutionPath) => StartAsync(solutionPath, beforeRunsStart: null);

    /// <summary>
    /// Like <see cref="StartAsync(string)"/>, but invokes <paramref name="beforeRunsStart"/> after worktree
    /// ownership is held and before file watching or the startup run begins. If the callback throws,
    /// ownership is released, no run starts and the exception propagates.
    /// </summary>
    /// <remarks>Hosts use this to bind their endpoints so an endpoint conflict does not start a run.</remarks>
    public async Task StartAsync(string solutionPath, Func<Task>? beforeRunsStart)
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
            await StartUnderGateAsync(solutionPath, beforeRunsStart).ConfigureAwait(false);
        }
        finally
        {
            _ = _startGate.Release();
        }
    }

    private async Task StartUnderGateAsync(string solutionPath, Func<Task>? beforeRunsStart)
    {
        if (_ownership is not null)
        {
            throw new InvalidOperationException("The engine has already been started.");
        }

        solutionPath = PathNormalizer.GetFullPath(solutionPath);
        _state.SolutionPath = solutionPath;
        _coordinationDirectory.RejectLegacyBudget();
        _coordinationDirectory.EnsureReady();
        _ownership = WorktreeOwnership.Acquire(
            _coordinationDirectory, solutionPath, _ownerHost, dashboardUrl: _ownerDashboardUrl);

        try
        {
            _cloneIdentity = CloneIdentity.FromWorktreePath(_ownership.Worktree.RootPath);
            _state.RepositoryTrustPath = _cloneIdentity.RootPath;
            if (_repositoryTrustStore is not null && _trustOnStart)
            {
                _repositoryTrustStore.Trust(_cloneIdentity);
            }

            var awaitingTrust = _requireTrust && !_repositoryTrustStore!.IsTrusted(_cloneIdentity);
            if (awaitingTrust)
            {
                _state.Phase = PistonPhase.AwaitingTrust;
                _state.NotifyChanged();
            }

            if (beforeRunsStart is not null)
            {
                await beforeRunsStart().ConfigureAwait(false);
            }

            if (!awaitingTrust)
            {
                await StartOrchestratorUnderGateAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            await _orchestrator.DisposeAsync().ConfigureAwait(false);
            CloseHistory();
            CloseSavedResults();
            _ownership.Dispose();
            _ownership = null;
            throw;
        }

    }

    private async Task StartOrchestratorUnderGateAsync()
    {
        if (_orchestratorStarted)
        {
            return;
        }

        // Only a trusted owner may touch the solution's .piston directory or evaluate its MSBuild projects.
        _diagnosticLog.Open();
        _diagnosticLog.Write("Engine",
            $"Worktree ownership: {_ownership!.Worktree.RootPath} (key {_ownership.Worktree.Key}); " +
            $"execution directory: {_coordinationDirectory.BaseDirectory}");
        OpenHistory(_solutionPath);
        OpenSavedResults(_solutionPath);
        await _orchestrator.StartAsync(_solutionPath).ConfigureAwait(false);
        _orchestratorStarted = true;
        _activitySink.Emit(new SolutionLoadedActivityEvent(_solutionPath, 0));
        RepositoryTrustGranted?.Invoke();
    }

    /// <summary>Grants trust for this clone and starts the deferred engine.</summary>
    public async Task GrantTrustAsync()
    {
        ThrowIfDisposing();
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
            if (!_requireTrust || _repositoryTrustStore is null || _cloneIdentity is null)
            {
                throw new InvalidOperationException("This engine is not awaiting repository trust.");
            }

            _repositoryTrustStore.Trust(_cloneIdentity);
            await StartOrchestratorUnderGateAsync().ConfigureAwait(false);
        }
        finally
        {
            _ = _startGate.Release();
        }
    }

    /// <summary>Starts the deferred engine if another process has granted trust.</summary>
    public async Task<bool> RefreshTrustAsync()
    {
        ThrowIfDisposing();
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
            if (!_requireTrust || _repositoryTrustStore is null || _cloneIdentity is null || _orchestratorStarted)
            {
                return true;
            }

            if (!_repositoryTrustStore.IsTrusted(_cloneIdentity))
            {
                return false;
            }

            await StartOrchestratorUnderGateAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _ = _startGate.Release();
        }
    }

    private void ThrowIfDisposing() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);

    private void ThrowIfTrustRequired()
    {
        if (_requireTrust && !_orchestratorStarted)
        {
            throw new RepositoryNotTrustedException(_cloneIdentity?.RootPath ?? _solutionDirectory);
        }
    }


    // Only the worktree owner writes history; a failure to open it leaves the engine without history.
    private void OpenHistory(string solutionPath)
    {
        if (!_historyEnabled)
        {
            return;
        }

        try
        {
            _history = RunHistoryStore.Open(
                _historyDirectory ?? HistoryLocation.DefaultBaseDirectory(),
                solutionPath,
                message => _diagnosticLog.Write("History", message));
            _orchestrator.RunHistory = _history;
            _diagnosticLog.Write("History", $"Run history: {_history.Location.FilePath}{(_history.ReadOnly ? " (read-only)" : "")}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ExecutionCoordinationException)
        {
            _diagnosticLog.Write("History", $"Run history is off: {ex.Message}");
        }
    }

    private void CloseHistory()
    {
        _orchestrator.RunHistory = null;
        _history?.Dispose();
        _history = null;
    }

    private void OpenSavedResults(string solutionPath)
    {
        if (!_persistResults)
        {
            return;
        }

        try
        {
            _savedResults = new SavedResultsStore(
                solutionPath,
                message => _diagnosticLog.Write("Results", message));
            _orchestrator.ResultsStore = _savedResults;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _diagnosticLog.Write("Results", $"Saved results are off: {ex.Message}");
        }
    }

    private void CloseSavedResults()
    {
        _orchestrator.ResultsStore = null;
        _savedResults?.Dispose();
        _savedResults = null;
    }

    public IReadOnlyList<TestHistory>? GetTestHistory(string test, string? projectId = null) => _history?.GetTestHistory(test, projectId);

    public IReadOnlyList<TestHistory>? GetFlakyTests() => _history?.GetFlakyTests();

    public IReadOnlyList<FailureClassification>? GetLatestFailures() => _history?.GetLatestFailures();

    public IReadOnlyList<StoredRunSummary>? GetStoredRuns() => _history?.GetRuns();

    public StoredRun? GetStoredRun(string runId) => _history?.GetRun(runId);

    public string? GetTestArtifact(string runId, int index)
    {
        var attachment = (_state.CurrentRun?.Tests ?? [])
            .Concat(_state.RecentRuns.SelectMany(run => run.Tests))
            .Concat(_state.TestSuites.SelectMany(suite => suite.Tests))
            .SelectMany(test => test.Attachments)
            .FirstOrDefault(candidate => candidate.RunId == runId && candidate.Index == index);
        return attachment is null ? null : _artifactStore.Resolve(attachment);
    }

    public Task ForceRunAsync()
    {
        ThrowIfTrustRequired();
        return _orchestrator.ForceRunAsync();
    }

    public Task<TestRunRecord?> RequestRunAsync()
    {
        ThrowIfTrustRequired();
        return _orchestrator.RequestRunAsync();
    }

    public Task<TestRunRecord> RunSelectedAsync(TestSelection selection)
    {
        ThrowIfTrustRequired();
        return _orchestrator.RunSelectedAsync(selection);
    }

    public async Task ResetAsync()
    {
        ThrowIfTrustRequired();
        await _orchestrator.ResetAsync().ConfigureAwait(false);
        if (_state.SolutionPath is { } solutionPath)
        {
            _activitySink.Emit(new SolutionLoadedActivityEvent(solutionPath, 0));
        }
    }

    public void Stop()
    {
        if (!_requireTrust || _orchestratorStarted)
        {
            _orchestrator.Stop();
        }
    }

    public void Pause()
    {
        ThrowIfTrustRequired();
        _orchestrator.Pause();
    }

    public void Resume()
    {
        ThrowIfTrustRequired();
        _orchestrator.Resume();
    }

    public Task<Verification.VerificationResult> VerifyAsync(TimeSpan timeout, CancellationToken ct)
    {
        ThrowIfTrustRequired();
        return _orchestrator.VerifyAsync(timeout, ct);
    }

    public void SetFilter(string? filter)
    {
        _state.TestFilter = filter;
        _state.NotifyChanged();
    }

    public void ClearResults()
    {
        ThrowIfTrustRequired();
        _orchestrator.ClearResults(_solutionDirectory, _solutionPath);
    }

    public CoverageReportSnapshot? GetCoverageReport() => _orchestrator.GetCoverageReport();

    public FileCoverageSnapshot? GetFileCoverage(string filePath) =>
        _orchestrator.GetFileCoverage(Path.GetFullPath(filePath, _solutionDirectory));

    public IReadOnlyList<CoveringTest>? GetCoveringTests(string filePath) =>
        _orchestrator.GetCoveringTests(Path.GetFullPath(filePath, _solutionDirectory));

    public IReadOnlyList<string> GetTestProjectPaths() => _orchestrator.GetTestProjectPaths();

    public ImpactAnalysisResult PlanAffected(IReadOnlyList<string> filePaths) =>
        WatchedFiles(filePaths) is { Count: > 0 } watched ? _orchestrator.PlanAffected(watched) : NoWatchedFileChanged;

    public Task<AffectedRun> RunAffectedAsync(IReadOnlyList<string> filePaths, CancellationToken ct) =>
        WatchedFiles(filePaths) is { Count: > 0 } watched
            ? _orchestrator.RunAffectedAsync(watched, ct)
            : Task.FromResult(new AffectedRun(NoWatchedFileChanged, null, Reused: false));

    private static ImpactAnalysisResult NoWatchedFileChanged =>
        new([], [], RequiresGraphRebuild: false, IsFullRun: false)
        {
            Explanation = "No watched file changed, so nothing would run.",
        };

    // Mirror the watcher: changes under bin, obj and other excluded directories trigger nothing.
    private List<string> WatchedFiles(IReadOnlyList<string> filePaths) =>
        [.. filePaths
            .Select(p => Path.GetFullPath(p, _solutionDirectory))
            .Where(p => !File.Exists(p) || SourceFingerprinter.CoversPath(_solutionDirectory, p))];

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        _ = Interlocked.Exchange(ref _disposeRequested, 1);
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await _orchestrator.DisposeAsync().ConfigureAwait(false);
            if (_warmHosts is not null)
            {
                await _warmHosts.DisposeAsync().ConfigureAwait(false);
            }

            CloseSavedResults();
            CloseHistory();
            _coverageStore?.Dispose();
            _pool.Dispose();
            // Released after the orchestrator so no run executes without ownership.
            _ownership?.Dispose();
            _ownership = null;
            _diagnosticLog.Dispose();
            _disposed = true;
        }
        finally
        {
            _ = _startGate.Release();
        }
    }
}
