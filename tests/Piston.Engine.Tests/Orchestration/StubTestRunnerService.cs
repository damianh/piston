// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Tests.Orchestration;

internal sealed class StubTestRunnerService(IReadOnlyList<string> projectPaths, TimeSpan delay) : ITestRunnerService
{
    public int FailureCount { get; set; }

    private readonly TaskCompletionSource _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForCompletionAsync() => _completionSource.Task;

    public Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        string? filter,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct) =>
        RunTestsAsync(solutionPath, null, filter, false, onProgress, null, ct);

    public Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        IReadOnlyList<string>? testProjectPaths,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct) =>
        RunTestsAsync(solutionPath, testProjectPaths, filter, collectCoverage, onProgress, null, ct);

    public async Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        IReadOnlyList<string>? testProjectPaths,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        Action<ProjectTestResult>? onProjectCompleted,
        CancellationToken ct)
    {
        var paths = testProjectPaths ?? projectPaths;
        var allSuites = new List<TestSuite>();

        foreach (var path in paths)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            var tests = FailureCount > 0
                ? Enumerable.Range(0, FailureCount).Select(i => new TestResult(
                    $"{path}.Test{i}", $"Test{i}", TestStatus.Failed, TimeSpan.Zero, null,
                    new string('x', RetentionLimits.MaxActivityMessageLength * 2), null, null)).ToList()
                : [new TestResult(path + ".Test1", "Test1", TestStatus.Passed, TimeSpan.Zero, null, null, null, null)];
            var suite = new TestSuite(path, tests, DateTimeOffset.UtcNow, TimeSpan.Zero);

            allSuites.Add(suite);

            var projectResult = new ProjectTestResult(path, [suite], null, [], false);
            onProjectCompleted?.Invoke(projectResult);
        }

        _ = _completionSource.TrySetResult();
        return new TestRunResult(allSuites, null, []);
    }
}
