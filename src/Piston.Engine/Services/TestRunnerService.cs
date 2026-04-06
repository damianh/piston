using Piston.Engine.Models;

namespace Piston.Engine.Services;

public sealed class TestRunnerService : ITestRunnerService
{
    private readonly ITestExecutionStrategy _strategy;
    private readonly ITestProcessPool? _pool;

    public TestRunnerService(ITestExecutionStrategy strategy)
    {
        _strategy = strategy;
    }

    public TestRunnerService(ITestExecutionStrategy strategy, ITestProcessPool pool)
    {
        _strategy = strategy;
        _pool     = pool;
    }

    public Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        string? filter,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct) =>
        RunTestsAsync(solutionPath, null, filter, collectCoverage: false, onProgress, onProjectCompleted: null, ct);

    public Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        IReadOnlyList<string>? testProjectPaths,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct) =>
        RunTestsAsync(solutionPath, testProjectPaths, filter, collectCoverage, onProgress, onProjectCompleted: null, ct);

    public async Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        IReadOnlyList<string>? testProjectPaths,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        Action<ProjectTestResult>? onProjectCompleted,
        CancellationToken ct)
    {
        if (testProjectPaths is null || testProjectPaths.Count == 0)
        {
            // Single target (whole solution path) — run directly
            var request = new ProjectTestRequest(solutionPath, filter, collectCoverage);
            var single  = await _strategy.ExecuteAsync(request, onProgress, ct).ConfigureAwait(false);
            return new TestRunResult(single.Suites, single.RunnerError, single.CoverageReportPaths);
        }

        // Multiple projects — use pool if available, otherwise sequential
        if (_pool is not null && testProjectPaths.Count > 1)
        {
            return await RunWithPoolAsync(testProjectPaths, filter, collectCoverage, onProgress, onProjectCompleted, ct)
                .ConfigureAwait(false);
        }

        return await RunSequentialAsync(testProjectPaths, filter, collectCoverage, onProgress, onProjectCompleted, ct)
            .ConfigureAwait(false);
    }

    private async Task<TestRunResult> RunWithPoolAsync(
        IReadOnlyList<string> testProjectPaths,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        Action<ProjectTestResult>? onProjectCompleted,
        CancellationToken ct)
    {
        var requests = testProjectPaths
            .Select(p => new ProjectTestRequest(p, filter, collectCoverage))
            .ToList();

        // Wrap the onProgress callback so it can fire from concurrent projects
        Action<ProjectTestResult>? wrappedOnProjectCompleted = onProjectCompleted is not null || onProgress is not null
            ? result =>
            {
                onProjectCompleted?.Invoke(result);
            }
            : null;

        var results = await _pool!.RunProjectsAsync(requests, wrappedOnProjectCompleted, ct)
            .ConfigureAwait(false);

        var allSuites = new List<TestSuite>();
        var allCoveragePaths = new List<string>();
        string? lastRunnerError = null;

        foreach (var result in results)
        {
            allSuites.AddRange(result.Suites);
            allCoveragePaths.AddRange(result.CoverageReportPaths);
            if (result.RunnerError is not null)
                lastRunnerError = result.RunnerError;
        }

        return new TestRunResult(allSuites, lastRunnerError, allCoveragePaths);
    }

    private async Task<TestRunResult> RunSequentialAsync(
        IReadOnlyList<string> testProjectPaths,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        Action<ProjectTestResult>? onProjectCompleted,
        CancellationToken ct)
    {
        var allSuites = new List<TestSuite>();
        var allCoveragePaths = new List<string>();
        string? lastRunnerError = null;

        foreach (var projectPath in testProjectPaths)
        {
            if (ct.IsCancellationRequested)
                return new TestRunResult(allSuites, null, allCoveragePaths);

            try
            {
                var request = new ProjectTestRequest(projectPath, filter, collectCoverage);
                var result  = await _strategy.ExecuteAsync(request, onProgress, ct).ConfigureAwait(false);

                allSuites.AddRange(result.Suites);
                allCoveragePaths.AddRange(result.CoverageReportPaths);
                if (result.RunnerError is not null)
                    lastRunnerError = result.RunnerError;

                onProjectCompleted?.Invoke(result);
            }
            catch (OperationCanceledException)
            {
                return new TestRunResult(allSuites, null, allCoveragePaths);
            }
        }

        return new TestRunResult(allSuites, lastRunnerError, allCoveragePaths);
    }
}

