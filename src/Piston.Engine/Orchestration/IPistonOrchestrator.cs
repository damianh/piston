// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Orchestration;

public interface IPistonOrchestrator : IDisposable
{
    Task StartAsync(string solutionPath);
    Task ForceRunAsync();
    Task<Models.TestRunRecord?> RequestRunAsync();
    Task<Models.TestRunRecord> RunSelectedAsync(Models.TestSelection selection);
    void Stop();
}
