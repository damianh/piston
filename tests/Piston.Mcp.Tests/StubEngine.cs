using Piston.Engine;
using Piston.Engine.Models;

namespace Piston.Mcp.Tests;

internal sealed class StubEngine : IEngine
{
    public PistonState State { get; } = new();
    public bool ForceRunCalled { get; private set; }
    public string? LastFilter { get; private set; }
    public bool ClearResultsCalled { get; private set; }
    public PistonPhase CompletedPhase { get; set; } = PistonPhase.Idle;
    public Task ForceRunCompletion { get; set; } = Task.CompletedTask;

    public Task StartAsync(string solutionPath)
    {
        State.SolutionPath = solutionPath;
        return Task.CompletedTask;
    }

    public async Task ForceRunAsync()
    {
        ForceRunCalled = true;
        await ForceRunCompletion;
        State.Phase = CompletedPhase;
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
