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
    private readonly MachineExecutionCoordinator _executionCoordinator;
    private readonly string? _ownerHost;
    private readonly bool _historyEnabled;
    private readonly MtpWarmHostPool? _warmHosts;
    private readonly string? _historyDirectory;
    private WorktreeOwnership? _ownership;
    private RunHistoryStore? _history;

    public PistonEngine(PistonOptions options)
        : this(options, NullActivityEventSink.Instance)
    {
    }

    public PistonEngine(PistonOptions options, IActivityEventSink activitySink)
    {
        _activitySink = activitySink;
        _ownerHost = options.OwnerHost;
        _historyEnabled = options.HistoryEnabled;
        _historyDirectory = options.HistoryDirectory;

        // Register MSBuild before any Microsoft.Build.* types are loaded.
        // This must happen before MsBuildSolutionGraph (or any class referencing MSBuild) is JIT-compiled.
        MsBuildLocatorGuard.EnsureRegistered();

        // Initialize diagnostic log early so all components can use it; the file is opened once ownership is held.
        var solutionDir = Path.GetDirectoryName(options.SolutionPath)
            ?? throw new ArgumentException("Cannot resolve solution directory.", nameof(options));
        _solutionDirectory = solutionDir;
        _diagnosticLog = DiagnosticLog.Initialize(solutionDir);
        _diagnosticLog.Write("Engine", $"SolutionPath: {options.SolutionPath}");
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
            var coverageStore = new SqliteCoverageStore();
            _coverageStore = coverageStore;
            coverageProcessor = new CoverageProcessor(coberturaParser);
        }

        var impactAnalyzer = new ImpactAnalyzer(
            solutionPath => new MsBuildSolutionGraph(solutionPath),
            _coverageStore);

        var vsTestStrategy = new ProcessTestExecutionStrategy(trxParser);
        _warmHosts = options.WarmTestHosts ? new MtpWarmHostPool() : null;
        var buildService = new BuildService(_warmHosts);
        var mtpStrategy = new MtpTestExecutionStrategy(
            projectPath => impactAnalyzer.GetMtpOutputPath(projectPath),
            solutionDir,
            impactAnalyzer.IsMultiTargeted,
            _warmHosts);

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

        // Each admitted run reserves this daemon's test-process parallelism from the machine budget.
        var coordinationDirectory = options.ExecutionCoordinationDirectory is { } configuredDirectory
            ? new ExecutionCoordinationDirectory(configuredDirectory)
            : ExecutionCoordinationDirectory.ResolveDefault();
        _executionCoordinator = new MachineExecutionCoordinator(
            coordinationDirectory,
            new ExecutionReservation(effectivePoolSize, options.ExecutionMemoryReservationMb));

        var testRunner = new TestRunnerService(strategy, _pool);

        _orchestrator = new PistonOrchestrator(
            fileWatcher,
            buildService,
            testRunner,
            impactAnalyzer,
            _state,
            _coverageStore,
            coverageProcessor,
            options.CoverageEnabled,
            activitySink,
            _executionCoordinator);

        var sdkVersions = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<string?>>(StringComparer.Ordinal);
        _orchestrator.BackfillGranularity = options.ImpactBackfill switch
        {
            ImpactBackfillMode.Class => Impact.ImpactGranularity.Class,
            ImpactBackfillMode.Test => Impact.ImpactGranularity.Test,
            _ => null,
        };
        _orchestrator.HangThreshold = options.HangThreshold;
        _orchestrator.SdkVersionProvider = dir => sdkVersions.GetOrAdd(dir, d => new Lazy<string?>(() => ProbeSdkVersion(d))).Value;
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
                WorkingDirectory = workingDirectory,
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

    /// <summary>Worktree ownership held by this engine; null before <see cref="StartAsync"/>.</summary>
    internal WorktreeOwnership? Ownership => _ownership;

    public ExecutionOwnerInfo? OwnerInfo => _ownership?.Owner;

    /// <summary>Advertises a bound dashboard while this engine holds worktree ownership.</summary>
    public void PublishDashboardUrl(string? url) => _ownership?.PublishDashboardUrl(url);

    /// <summary>
    /// Acquires exclusive ownership of the solution's worktree and validates the machine execution
    /// budget, then starts watching and runs the initial build/test.
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
        if (_ownership is not null)
        {
            throw new InvalidOperationException("The engine has already been started.");
        }

        var budget = _executionCoordinator.Initialize();
        _ownership = WorktreeOwnership.Acquire(_executionCoordinator.Directory, solutionPath, _ownerHost);
        // Only the owner may touch the worktree's .piston directory.
        _diagnosticLog.Open();
        _diagnosticLog.Write("Engine",
            $"Worktree ownership: {_ownership.Worktree.RootPath} (key {_ownership.Worktree.Key}); " +
            $"execution directory: {_executionCoordinator.Directory.BaseDirectory}; " +
            $"budget: {budget.CpuSlots} CPU slot(s), memory {budget.MemoryMb?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not budgeted"} ({budget.Source}); " +
            $"reservation: {_executionCoordinator.Reservation.CpuSlots} CPU slot(s), memory {_executionCoordinator.Reservation.MemoryMb?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");

        OpenHistory(solutionPath);

        try
        {
            if (beforeRunsStart is not null)
            {
                await beforeRunsStart().ConfigureAwait(false);
            }

            await _orchestrator.StartAsync(solutionPath).ConfigureAwait(false);
        }
        catch
        {
            await _orchestrator.DisposeAsync().ConfigureAwait(false);
            CloseHistory();
            _ownership.Dispose();
            _ownership = null;
            throw;
        }

        _activitySink.Emit(new SolutionLoadedActivityEvent(solutionPath, 0));
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

    public IReadOnlyList<TestHistory>? GetTestHistory(string test, string? project = null) => _history?.GetTestHistory(test, project);

    public IReadOnlyList<TestHistory>? GetFlakyTests() => _history?.GetFlakyTests();

    public IReadOnlyList<FailureClassification>? GetLatestFailures() => _history?.GetLatestFailures();

    public IReadOnlyList<StoredRunSummary>? GetStoredRuns() => _history?.GetRuns();

    public StoredRun? GetStoredRun(string runId) => _history?.GetRun(runId);

    public Task ForceRunAsync() => _orchestrator.ForceRunAsync();

    public Task<TestRunRecord?> RequestRunAsync() => _orchestrator.RequestRunAsync();

    public Task<TestRunRecord> RunSelectedAsync(TestSelection selection) => _orchestrator.RunSelectedAsync(selection);

    public async Task ResetAsync()
    {
        await _orchestrator.ResetAsync().ConfigureAwait(false);
        if (_state.SolutionPath is { } solutionPath)
        {
            _activitySink.Emit(new SolutionLoadedActivityEvent(solutionPath, 0));
        }
    }

    public void Stop() => _orchestrator.Stop();

    public Task<Verification.VerificationResult> VerifyAsync(TimeSpan timeout, CancellationToken ct) =>
        _orchestrator.VerifyAsync(timeout, ct);

    public void SetFilter(string? filter)
    {
        _state.TestFilter = filter;
        _state.NotifyChanged();
    }

    public void ClearResults() => _orchestrator.ClearResults(_solutionDirectory);

    public CoverageReportSnapshot? GetCoverageReport() => _orchestrator.GetCoverageReport();

    public FileCoverageSnapshot? GetFileCoverage(string filePath) =>
        _orchestrator.GetFileCoverage(Path.GetFullPath(filePath, _solutionDirectory));

    public IReadOnlyList<CoveringTest>? GetCoveringTests(string filePath) =>
        _orchestrator.GetCoveringTests(Path.GetFullPath(filePath, _solutionDirectory));

    public ImpactAnalysisResult PlanAffected(IReadOnlyList<string> filePaths)
    {
        // Mirror the watcher: changes under bin, obj and other excluded directories trigger nothing.
        var watched = filePaths
            .Select(p => Path.GetFullPath(p, _solutionDirectory))
            .Where(p => !File.Exists(p) || SourceFingerprinter.CoversPath(_solutionDirectory, p))
            .ToList();
        return watched.Count > 0
            ? _orchestrator.PlanAffected(watched)
            : new ImpactAnalysisResult([], [], RequiresGraphRebuild: false, IsFullRun: false)
            {
                Explanation = "No watched file changed, so nothing would run.",
            };
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        await _orchestrator.DisposeAsync().ConfigureAwait(false);
        if (_warmHosts is not null)
        {
            await _warmHosts.DisposeAsync().ConfigureAwait(false);
        }

        CloseHistory();
        _coverageStore?.Dispose();
        _pool.Dispose();
        // Released after the orchestrator so no run executes without ownership.
        _ownership?.Dispose();
        _ownership = null;
        _diagnosticLog.Dispose();
    }
}
