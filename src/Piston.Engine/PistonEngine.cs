// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Execution;
using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Piston.Protocol.Messages;

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
    private WorktreeOwnership? _ownership;

    public PistonEngine(PistonOptions options)
        : this(options, NullActivityEventSink.Instance)
    {
    }

    public PistonEngine(PistonOptions options, IActivityEventSink activitySink)
    {
        _activitySink = activitySink;

        // Register MSBuild before any Microsoft.Build.* types are loaded.
        // This must happen before MsBuildSolutionGraph (or any class referencing MSBuild) is JIT-compiled.
        MsBuildLocatorGuard.EnsureRegistered();

        // Initialize diagnostic log early so all components can use it.
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
        var buildService = new BuildService();
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
        var mtpStrategy = new MtpTestExecutionStrategy(
            projectPath => impactAnalyzer.GetMtpOutputPath(projectPath),
            solutionDir);

        ITestExecutionStrategy strategy = options.TestExecutionMode switch
        {
            TestExecutionMode.InProcess =>
                throw new NotSupportedException("InProcess test execution mode is not yet implemented."),
            _ => new CompositeTestExecutionStrategy(mtpStrategy, vsTestStrategy),
        };

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

        var sdkVersion = new Lazy<string?>(ProbeSdkVersion);
        _orchestrator.SdkVersionProvider = () => sdkVersion.Value;
    }

    /// <summary>Runs <c>dotnet --version</c> in the daemon's working directory (same as build/test processes).</summary>
    private static string? ProbeSdkVersion()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "--version")
            {
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

    /// <summary>
    /// Acquires exclusive ownership of the solution's worktree and validates the machine execution
    /// budget, then starts watching and runs the initial build/test.
    /// </summary>
    /// <exception cref="ExecutionCoordinationException">
    /// Another live daemon owns the worktree, or machine-wide coordination is unavailable or misconfigured.
    /// </exception>
    public async Task StartAsync(string solutionPath)
    {
        if (_ownership is not null)
        {
            throw new InvalidOperationException("The engine has already been started.");
        }

        var budget = _executionCoordinator.Initialize();
        _ownership = WorktreeOwnership.Acquire(_executionCoordinator.Directory, solutionPath);
        _diagnosticLog.Write("Engine",
            $"Worktree ownership: {_ownership.Worktree.RootPath} (key {_ownership.Worktree.Key}); " +
            $"execution directory: {_executionCoordinator.Directory.BaseDirectory}; " +
            $"budget: {budget.CpuSlots} CPU slot(s), memory {budget.MemoryMb?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not budgeted"} ({budget.Source}); " +
            $"reservation: {_executionCoordinator.Reservation.CpuSlots} CPU slot(s), memory {_executionCoordinator.Reservation.MemoryMb?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");

        try
        {
            await _orchestrator.StartAsync(solutionPath).ConfigureAwait(false);
        }
        catch
        {
            await _orchestrator.DisposeAsync().ConfigureAwait(false);
            _ownership.Dispose();
            _ownership = null;
            throw;
        }

        _activitySink.Emit(ActivityEventFactory.SolutionLoaded(solutionPath, 0));
    }

    public Task ForceRunAsync() => _orchestrator.ForceRunAsync();

    public Task<TestRunRecord?> RequestRunAsync() => _orchestrator.RequestRunAsync();

    public void Stop() => _orchestrator.Stop();

    public void SetFilter(string? filter)
    {
        _state.TestFilter = filter;
        _state.NotifyChanged();
    }

    public void ClearResults() => _orchestrator.ClearResults(_solutionDirectory);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        await _orchestrator.DisposeAsync().ConfigureAwait(false);
        _coverageStore?.Dispose();
        _pool.Dispose();
        // Released after the orchestrator so no run executes without ownership.
        _ownership?.Dispose();
        _ownership = null;
        _diagnosticLog.Dispose();
    }
}
