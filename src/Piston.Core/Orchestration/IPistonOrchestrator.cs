namespace Piston.Core.Orchestration;

public interface IPistonOrchestrator : IDisposable
{
    Task StartAsync(string solutionPath);
    Task ForceRunAsync();
    void Stop();
}
