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

    /// <summary>
    /// Re-initialises the engine without restarting the process: cancels the active run, clears results
    /// and coverage, reloads the solution graph, restarts file monitoring and starts a full run that
    /// re-discovers all tests. Returns once the run is requested.
    /// </summary>
    Task ResetAsync() => throw new NotSupportedException("Reset is not supported by this engine.");

    /// <summary>
    /// Produces current full verification for the present source, reusing or awaiting qualifying runs
    /// before requesting a forced full run. See <see cref="Verification.VerificationPolicy"/>.
    /// </summary>
    Task<Verification.VerificationResult> VerifyAsync(TimeSpan timeout, CancellationToken ct) =>
        throw new NotSupportedException("Verification is not supported by this engine.");

    // State queries
    PistonState State { get; }

    // Configuration
    void SetFilter(string? filter);
    void ClearResults();
}
