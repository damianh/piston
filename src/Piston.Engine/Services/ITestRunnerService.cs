using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>Result of a test run, including authoritative suites and any runner-level error output.</summary>
public sealed record TestRunResult(
    IReadOnlyList<TestSuite> Suites,
    string? RunnerError,
    IReadOnlyList<string> CoverageReportPaths);

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

    /// <summary>
    /// Runs tests for specific test projects when <paramref name="testProjectPaths"/> is provided,
    /// or all test projects in the solution when <paramref name="testProjectPaths"/> is null.
    /// </summary>
    /// <param name="solutionPath">Path to the .sln or .slnx file (used when testProjectPaths is null).</param>
    /// <param name="testProjectPaths">
    /// Specific test project paths to run. When null, runs the entire solution.
    /// </param>
    /// <param name="filter">Optional dotnet-test filter expression. Null means run all tests.</param>
    /// <param name="collectCoverage">When true, adds <c>--collect "XPlat Code Coverage"</c> to the dotnet test args.</param>
    /// <param name="onProgress">Live progress callback. May be null.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        IReadOnlyList<string>? testProjectPaths,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct);

    /// <summary>
    /// Runs tests with per-project progress reporting. Each project completion fires onProjectCompleted.
    /// </summary>
    Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        IReadOnlyList<string>? testProjectPaths,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        Action<ProjectTestResult>? onProjectCompleted,
        CancellationToken ct);
}

