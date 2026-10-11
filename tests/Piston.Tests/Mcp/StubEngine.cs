// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine;
using Piston.Engine.Models;

namespace Piston.Tests.Mcp;

internal sealed class StubEngine : IEngine
{
    public PistonState State { get; } = new();
    public bool ForceRunCalled { get; private set; }
    public string? LastFilter { get; private set; }
    public bool ClearResultsCalled { get; private set; }
    public PistonPhase CompletedPhase { get; set; } = PistonPhase.Idle;
    public Task ForceRunCompletion { get; set; } = Task.CompletedTask;

    /// <summary>Run record published as the latest finished run when ForceRunAsync completes.</summary>
    public TestRunRecord? NextRun { get; set; }

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
        if (NextRun is not null)
        {
            State.LastRun = NextRun;
            State.RecentRuns = [NextRun, .. State.RecentRuns];
        }
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
