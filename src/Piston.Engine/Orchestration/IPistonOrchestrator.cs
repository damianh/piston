namespace Piston.Engine.Orchestration;

public interface IPistonOrchestrator : IDisposable
{
    Task StartAsync(string solutionPath);
    Task ForceRunAsync();
    Task<Models.TestRunRecord?> RequestRunAsync();
    void Stop();
}
