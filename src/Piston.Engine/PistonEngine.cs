using Piston.Engine.Coverage;
using Piston.Engine.Impact;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;

namespace Piston.Engine;

public sealed class PistonEngine : IEngine
{
    private readonly PistonState _state;
    private readonly PistonOrchestrator _orchestrator;
    private readonly ICoverageStore? _coverageStore;
    private readonly ITestProcessPool _pool;
    private readonly DiagnosticLog _diagnosticLog;

    public PistonEngine(PistonOptions options)
    {
        // Register MSBuild before any Microsoft.Build.* types are loaded.
        // This must happen before MsBuildSolutionGraph (or any class referencing MSBuild) is JIT-compiled.
        MsBuildLocatorGuard.EnsureRegistered();

        // Initialize diagnostic log early so all components can use it.
        var solutionDir = Path.GetDirectoryName(options.SolutionPath)
            ?? throw new ArgumentException("Cannot resolve solution directory.", nameof(options));
        _diagnosticLog = DiagnosticLog.Initialize(solutionDir);
        _diagnosticLog.Write("Engine", $"SolutionPath: {options.SolutionPath}");
        _diagnosticLog.Write("Engine", $"TestExecutionMode: {options.TestExecutionMode}");
        _diagnosticLog.Write("Engine", $"CoverageEnabled: {options.CoverageEnabled}");
        _diagnosticLog.Write("Engine", $"ProcessPoolSize: {options.ProcessPoolSize}");
        _diagnosticLog.Write("Engine", $"TestFilter: {options.TestFilter ?? "(none)"}");

        _state = new PistonState
        {
            TestFilter      = options.TestFilter,
            CoverageEnabled = options.CoverageEnabled,
        };

        var fileWatcher = new FileWatcherService(options.DebounceInterval);
        var buildService = new BuildService();
        var trxParser = new TrxResultParser();

        ICoverageProcessor? coverageProcessor = null;

        if (options.CoverageEnabled)
        {
            var coberturaParser = new CoberturaParser();
            var coverageStore   = new SqliteCoverageStore();
            _coverageStore      = coverageStore;
            coverageProcessor   = new CoverageProcessor(coberturaParser);
        }

        var impactAnalyzer = new ImpactAnalyzer(
            solutionPath => new MsBuildSolutionGraph(solutionPath),
            _coverageStore);

        var vsTestStrategy = new ProcessTestExecutionStrategy(trxParser);
        var mtpStrategy    = new MtpTestExecutionStrategy(
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

        var testRunner = new TestRunnerService(strategy, _pool);

        _orchestrator = new PistonOrchestrator(
            fileWatcher,
            buildService,
            testRunner,
            impactAnalyzer,
            _state,
            _coverageStore,
            coverageProcessor,
            options.CoverageEnabled);
    }

    public PistonState State => _state;

    public Task StartAsync(string solutionPath) => _orchestrator.StartAsync(solutionPath);
    public Task ForceRunAsync() => _orchestrator.ForceRunAsync();
    public void Stop() => _orchestrator.Stop();

    public void SetFilter(string? filter)
    {
        _state.TestFilter = filter;
        _state.NotifyChanged();
    }

    public void ClearResults()
    {
        _state.TestSuites = [];
        _state.LastRunTime = null;
        _state.NotifyChanged();
    }

    public void Dispose()
    {
        _orchestrator.Dispose();
        _coverageStore?.Dispose();
        _pool.Dispose();
        _diagnosticLog.Dispose();
    }
}
