namespace Piston.Engine;

public interface IEngine : IDisposable
{
    // Lifecycle
    Task StartAsync(string solutionPath);
    Task ForceRunAsync();
    void Stop();

    // State queries
    PistonState State { get; }

    // Configuration
    void SetFilter(string? filter);
    void ClearResults();
}
