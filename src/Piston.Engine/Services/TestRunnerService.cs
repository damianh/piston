// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

public sealed class TestRunnerService : ITestRunnerService
{
    private readonly ITestExecutionStrategy _strategy;
    private readonly ITestProcessPool? _pool;

    // Without a pool, discovery and execution still share a single test-process slot.
    private readonly SemaphoreSlim _soloSlot = new(1, 1);

    public TestRunnerService(ITestExecutionStrategy strategy) => _strategy = strategy;

    public TestRunnerService(ITestExecutionStrategy strategy, ITestProcessPool pool)
    {
        _strategy = strategy;
        _pool = pool;
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

    public Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        IReadOnlyList<string>? testProjectPaths,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        Action<ProjectTestResult>? onProjectCompleted,
        CancellationToken ct) =>
        RunTestsAsync(
            solutionPath,
            testProjectPaths,
            filter,
            collectCoverage,
            new TestRunCallbacks
            {
                OnProjectProgress = onProgress is null ? null : (_, suites) => onProgress(suites),
                OnProjectCompleted = onProjectCompleted,
            },
            ct);

    public async Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        IReadOnlyList<string>? testProjectPaths,
        string? filter,
        bool collectCoverage,
        TestRunCallbacks callbacks,
        CancellationToken ct)
    {
        if (testProjectPaths is null || testProjectPaths.Count == 0)
        {
            // Single target (whole solution path) — run directly
            var request = new ProjectTestRequest(solutionPath, filter, collectCoverage);
            ProjectTestResult single;
            IDisposable slot;
            try
            {
                slot = await AcquireSlotAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new TestRunResult([], null, []);
            }

            using (slot)
            {
                callbacks.OnProjectStarted?.Invoke(solutionPath);
                single = await _strategy.ExecuteAsync(request, ProgressFor(callbacks, solutionPath), ct).ConfigureAwait(false);
            }

            callbacks.OnProjectCompleted?.Invoke(single);
            return new TestRunResult(single.Suites, single.RunnerError, single.CoverageReportPaths);
        }

        // Multiple projects — use pool if available, otherwise sequential
        if (_pool is not null && testProjectPaths.Count > 1)
        {
            return await RunWithPoolAsync(testProjectPaths, filter, collectCoverage, callbacks, ct)
                .ConfigureAwait(false);
        }

        return await RunSequentialAsync(testProjectPaths, filter, collectCoverage, callbacks, ct)
            .ConfigureAwait(false);
    }

    public async Task DiscoverTestsAsync(
        IReadOnlyList<string> testProjectPaths,
        string? filter,
        Action<string, IReadOnlyList<TestResult>> onDiscovered,
        CancellationToken ct)
    {
        // Discovery runs alongside execution, so it draws on the same slots to stay within capacity.
        async Task DiscoverOneAsync(string projectPath)
        {
            IDisposable slot;
            try
            {
                slot = await AcquireSlotAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var tests = await _strategy.DiscoverAsync(
                    projectPath,
                    filter,
                    partial =>
                    {
                        if (partial.Count > 0 && !ct.IsCancellationRequested)
                        {
                            onDiscovered(projectPath, partial);
                        }
                    },
                    ct).ConfigureAwait(false);
                if (tests.Count > 0 && !ct.IsCancellationRequested)
                {
                    onDiscovered(projectPath, tests);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                slot.Dispose();
            }
        }

        await Task.WhenAll(testProjectPaths.Select(DiscoverOneAsync)).ConfigureAwait(false);
    }

    private async Task<IDisposable> AcquireSlotAsync(CancellationToken ct)
    {
        if (_pool is not null)
        {
            return await _pool.AcquireSlotAsync(ct).ConfigureAwait(false);
        }

        await _soloSlot.WaitAsync(ct).ConfigureAwait(false);
        return new SoloSlotRelease(_soloSlot);
    }

    private static Action<IReadOnlyList<TestSuite>>? ProgressFor(TestRunCallbacks callbacks, string projectPath) =>
        callbacks.OnProjectProgress is { } onProjectProgress
            ? suites => onProjectProgress(projectPath, suites)
            : null;

    private async Task<TestRunResult> RunWithPoolAsync(
        IReadOnlyList<string> testProjectPaths,
        string? filter,
        bool collectCoverage,
        TestRunCallbacks callbacks,
        CancellationToken ct)
    {
        var requests = testProjectPaths
            .Select(p => new ProjectTestRequest(p, filter, collectCoverage))
            .ToList();

        var results = await _pool!.RunProjectsAsync(requests, callbacks, ct)
            .ConfigureAwait(false);

        var allSuites = new List<TestSuite>();
        var allCoveragePaths = new List<string>();
        string? lastRunnerError = null;

        foreach (var result in results)
        {
            allSuites.AddRange(result.Suites);
            allCoveragePaths.AddRange(result.CoverageReportPaths);
            if (result.RunnerError is not null)
            {
                lastRunnerError = result.RunnerError;
            }
        }

        return new TestRunResult(allSuites, lastRunnerError, allCoveragePaths);
    }

    private async Task<TestRunResult> RunSequentialAsync(
        IReadOnlyList<string> testProjectPaths,
        string? filter,
        bool collectCoverage,
        TestRunCallbacks callbacks,
        CancellationToken ct)
    {
        var allSuites = new List<TestSuite>();
        var allCoveragePaths = new List<string>();
        string? lastRunnerError = null;

        foreach (var projectPath in testProjectPaths)
        {
            if (ct.IsCancellationRequested)
            {
                return new TestRunResult(allSuites, null, allCoveragePaths);
            }

            try
            {
                var request = new ProjectTestRequest(projectPath, filter, collectCoverage);
                ProjectTestResult result;
                using (await AcquireSlotAsync(ct).ConfigureAwait(false))
                {
                    callbacks.OnProjectStarted?.Invoke(projectPath);
                    result = await _strategy.ExecuteAsync(request, ProgressFor(callbacks, projectPath), ct).ConfigureAwait(false);
                }

                allSuites.AddRange(result.Suites);
                allCoveragePaths.AddRange(result.CoverageReportPaths);
                if (result.RunnerError is not null)
                {
                    lastRunnerError = result.RunnerError;
                }

                callbacks.OnProjectCompleted?.Invoke(result);
            }
            catch (OperationCanceledException)
            {
                return new TestRunResult(allSuites, null, allCoveragePaths);
            }
        }

        return new TestRunResult(allSuites, lastRunnerError, allCoveragePaths);
    }

    private sealed class SoloSlotRelease(SemaphoreSlim slot) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _ = slot.Release();
            }
        }
    }
}
