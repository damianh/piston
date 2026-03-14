using Piston.Engine.Impact;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;

namespace Piston.Engine;

public sealed class PistonEngine : IEngine
{
    private readonly PistonState _state;
    private readonly PistonOrchestrator _orchestrator;

    public PistonEngine(PistonOptions options)
    {
        // Register MSBuild before any Microsoft.Build.* types are loaded.
        // This must happen before MsBuildSolutionGraph (or any class referencing MSBuild) is JIT-compiled.
        MsBuildLocatorGuard.EnsureRegistered();

        _state = new PistonState { TestFilter = options.TestFilter };
        var fileWatcher = new FileWatcherService(options.DebounceInterval);
        var buildService = new BuildService();
        var trxParser = new TrxResultParser();
        var testRunner = new TestRunnerService(trxParser);
        var impactAnalyzer = new ImpactAnalyzer(solutionPath => new MsBuildSolutionGraph(solutionPath));
        _orchestrator = new PistonOrchestrator(fileWatcher, buildService, testRunner, impactAnalyzer, _state);
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

    public void Dispose() => _orchestrator.Dispose();
}
