// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine;

public interface IEngine : IDisposable
{
    // Lifecycle
    Task StartAsync(string solutionPath);
    Task ForceRunAsync();

    /// <summary>
    /// Requests a full-scope run and returns that run's provenance record once it is terminal.
    /// The default implementation is for test doubles; it cannot bind the result to the request.
    /// </summary>
    async Task<Models.TestRunRecord?> RequestRunAsync()
    {
        await ForceRunAsync().ConfigureAwait(false);
        return State.LastRun;
    }
    void Stop();

    // State queries
    PistonState State { get; }

    // Configuration
    void SetFilter(string? filter);
    void ClearResults();
}
