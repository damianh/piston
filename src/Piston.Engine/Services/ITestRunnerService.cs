using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>Result of a test run, including authoritative suites and any runner-level error output.</summary>
public sealed record TestRunResult(
    IReadOnlyList<TestSuite> Suites,
    string? RunnerError);

public interface ITestRunnerService
{
    /// <summary>
    /// Runs tests for the given solution, streaming per-test progress via <paramref name="onProgress"/>.
    /// </summary>
    /// <param name="solutionPath">Path to the .sln or .slnx file.</param>
    /// <param name="filter">Optional dotnet-test filter expression (passed as --filter). Null means run all tests.</param>
    /// <param name="onProgress">
    /// Called periodically during the run with a live snapshot of suites reflecting current
    /// <see cref="TestStatus.Running"/> / <see cref="TestStatus.Passed"/> / <see cref="TestStatus.Failed"/> states.
    /// May be null if no live updates are needed.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        string? filter,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct);
}
