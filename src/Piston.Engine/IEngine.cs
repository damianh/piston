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
    /// Runs the selected tests; the task completes with the run's record. The selection is validated
    /// before the run starts, so an invalid selection or a run already in progress fails the call (or
    /// the returned task) without superseding the active run.
    /// </summary>
    Task<Models.TestRunRecord> RunSelectedAsync(Models.TestSelection selection) =>
        throw new NotSupportedException("Running selected tests is not supported by this engine.");

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

    /// <summary>Returns the solution-wide coverage report, or null when coverage is disabled.</summary>
    Coverage.CoverageReportSnapshot? GetCoverageReport() => null;

    /// <summary>
    /// Returns the coverage of one source file (absolute, or relative to the solution directory), or null
    /// when coverage is disabled or the file was not measured.
    /// </summary>
    Coverage.FileCoverageSnapshot? GetFileCoverage(string filePath) => null;

    /// <summary>
    /// Returns the tests recorded as executing one source file (absolute, or relative to the solution directory), with
    /// how precisely each was measured. Null when coverage is disabled or unsupported.
    /// </summary>
    IReadOnlyList<Coverage.CoveringTest>? GetCoveringTests(string filePath) => null;

    /// <summary>
    /// Returns what a run triggered by changes to <paramref name="filePaths"/> (absolute, or relative to the solution
    /// directory) would build and test, without running anything or changing any state. Null when unsupported.
    /// </summary>
    Models.ImpactAnalysisResult? PlanAffected(IReadOnlyList<string> filePaths) => null;

    // Configuration
    void SetFilter(string? filter);
    void ClearResults();
}
