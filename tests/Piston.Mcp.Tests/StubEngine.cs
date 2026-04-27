using Piston.Engine;
using Piston.Engine.Models;

namespace Piston.Mcp.Tests;

internal sealed class StubEngine : IEngine
{
    public PistonState State { get; } = new();
    public bool ForceRunCalled { get; private set; }
    public string? LastFilter { get; private set; }
    public bool ClearResultsCalled { get; private set; }

    public Task StartAsync(string solutionPath)
    {
        State.SolutionPath = solutionPath;
        return Task.CompletedTask;
    }

    public Task ForceRunAsync()
    {
        ForceRunCalled = true;
        // Immediately go to Idle to simulate completion
        State.Phase = PistonPhase.Idle;
        return Task.CompletedTask;
    }

    public void Stop() { }

    public void SetFilter(string? filter)
    {
        LastFilter = filter;
        State.TestFilter = filter;
    }

    public void ClearResults()
    {
        ClearResultsCalled = true;
        State.TestSuites = [];
        State.CompletedTests = 0;
        State.TotalExpectedTests = 0;
    }

    public void Dispose() { }
}
