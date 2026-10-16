// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>Result of a test run, including authoritative suites and any runner-level error output.</summary>
public sealed record TestRunResult(
    IReadOnlyList<TestSuite> Suites,
    string? RunnerError,
    IReadOnlyList<string> CoverageReportPaths);

/// <summary>Per-project callbacks raised while a test run is in flight. All members are optional.</summary>
public sealed record TestRunCallbacks
{
    /// <summary>Raised when a project's test process starts (after any pool queueing).</summary>
    public Action<string>? OnProjectStarted { get; init; }

    /// <summary>Raised with a live snapshot of a single project's results so far.</summary>
    public Action<string, IReadOnlyList<TestSuite>>? OnProjectProgress { get; init; }

    /// <summary>Raised with a project's authoritative results when its test process exits.</summary>
    public Action<ProjectTestResult>? OnProjectCompleted { get; init; }
}

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

    /// <summary>
    /// Runs tests reporting start, live progress and completion per project.
    /// When <paramref name="testProjectPaths"/> is null, progress is attributed to <paramref name="solutionPath"/>.
    /// </summary>
    Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        IReadOnlyList<string>? testProjectPaths,
        string? filter,
        bool collectCoverage,
        TestRunCallbacks callbacks,
        CancellationToken ct) =>
        RunTestsAsync(
            solutionPath,
            testProjectPaths,
            filter,
            collectCoverage,
            callbacks.OnProjectProgress is { } onProjectProgress
                ? suites => onProjectProgress(solutionPath, suites)
                : null,
            callbacks.OnProjectCompleted,
            ct);

    /// <summary>
    /// Lists the tests in each project without running them, invoking <paramref name="onDiscovered"/>
    /// per project as soon as its list is available. Projects whose runner cannot list tests are skipped.
    /// </summary>
    Task DiscoverTestsAsync(
        IReadOnlyList<string> testProjectPaths,
        Action<string, IReadOnlyList<TestResult>> onDiscovered,
        CancellationToken ct) => Task.CompletedTask;
}

