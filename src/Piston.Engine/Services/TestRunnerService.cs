// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

public sealed class TestRunnerService : ITestRunnerService
{
    private readonly ITestExecutionStrategy _strategy;
    private readonly ITestProcessPool? _pool;
    private readonly ITestResourceGate? _resources;

    // Without a pool, discovery and execution still share a single test-process slot.
    private readonly SemaphoreSlim _soloSlot = new(1, 1);

    public TestRunnerService(ITestExecutionStrategy strategy) => _strategy = strategy;

    public TestRunnerService(ITestExecutionStrategy strategy, ITestProcessPool pool)
    {
        _strategy = strategy;
        _pool = pool;
    }

    /// <summary>
    /// Runs each project only once it holds its exclusive test resources from <paramref name="resources"/>,
    /// acquired before a pool slot so waiting projects do not occupy slots.
    /// </summary>
    internal TestRunnerService(ITestExecutionStrategy strategy, ITestProcessPool? pool, ITestResourceGate? resources)
    {
        _strategy = strategy;
        _pool = pool;
        _resources = resources;
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
            var (resources, refused) = await AcquireResourcesAsync(solutionPath, callbacks, ct).ConfigureAwait(false);
            if (refused is not null)
            {
                if (ct.IsCancellationRequested)
                {
                    return new TestRunResult([], null, []);
                }

                callbacks.OnProjectCompleted?.Invoke(refused);
                return new TestRunResult([], refused.RunnerError, []) { Projects = [refused] };
            }

            using (resources)
            {
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
            }

            single = WithResources(single, resources);
            callbacks.OnProjectCompleted?.Invoke(single);
            return new TestRunResult(single.Suites, single.RunnerError, single.CoverageReportPaths) { Projects = [single] };
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

    /// <summary>
    /// Waits for <paramref name="target"/>'s exclusive test resources. Returns the lease (null without a
    /// gate), or a result to report instead of running: empty when cancelled, crashed when coordination failed.
    /// </summary>
    private async Task<(TestResourceLease? Lease, ProjectTestResult? Refused)> AcquireResourcesAsync(
        string target, TestRunCallbacks callbacks, CancellationToken ct)
    {
        if (_resources is null)
        {
            return (null, null);
        }

        try
        {
            var needs = _resources.Resolve(target);
            var lease = await _resources.AcquireAsync(
                needs,
                status => callbacks.OnProjectWaiting?.Invoke(target, status),
                ct).ConfigureAwait(false);
            try
            {
                callbacks.OnProjectResourcesAcquired?.Invoke(target);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
            return (lease, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, new ProjectTestResult(target, [], null, [], Crashed: false));
        }
        catch (ExecutionCoordinationException ex)
        {
            return (null, new ProjectTestResult(
                target, [], $"Could not acquire exclusive test resources: {ex.Message}", [], Crashed: true));
        }
    }

    private static ProjectTestResult WithResources(ProjectTestResult result, TestResourceLease? lease) =>
        lease is null || lease.Status is null ? result : result with { Resources = lease.Resources };

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

        async Task<ProjectTestResult> RunOneAsync(ProjectTestRequest request)
        {
            var (resources, refused) = await AcquireResourcesAsync(request.ProjectPath, callbacks, ct).ConfigureAwait(false);
            if (refused is not null)
            {
                callbacks.OnProjectCompleted?.Invoke(refused);
                return refused;
            }

            ProjectTestResult result;
            using (resources)
            {
                result = await _pool!.RunProjectAsync(request, callbacks, ct).ConfigureAwait(false);
            }

            result = WithResources(result, resources);
            callbacks.OnProjectCompleted?.Invoke(result);
            return result;
        }

        var results = await Task.WhenAll(requests.Select(RunOneAsync)).ConfigureAwait(false);

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

        return new TestRunResult(allSuites, lastRunnerError, allCoveragePaths) { Projects = results };
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
        var projects = new List<ProjectTestResult>();
        string? lastRunnerError = null;

        foreach (var projectPath in testProjectPaths)
        {
            if (ct.IsCancellationRequested)
            {
                return new TestRunResult(allSuites, null, allCoveragePaths) { Projects = projects };
            }

            try
            {
                var request = new ProjectTestRequest(projectPath, filter, collectCoverage);
                ProjectTestResult result;
                var (resources, refused) = await AcquireResourcesAsync(projectPath, callbacks, ct).ConfigureAwait(false);
                if (refused is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    result = refused;
                }
                else
                {
                    using (resources)
                    using (await AcquireSlotAsync(ct).ConfigureAwait(false))
                    {
                        callbacks.OnProjectStarted?.Invoke(projectPath);
                        result = await _strategy.ExecuteAsync(request, ProgressFor(callbacks, projectPath), ct).ConfigureAwait(false);
                    }

                    result = WithResources(result, resources);
                }

                projects.Add(result);
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
                return new TestRunResult(allSuites, null, allCoveragePaths) { Projects = projects };
            }
        }

        return new TestRunResult(allSuites, lastRunnerError, allCoveragePaths) { Projects = projects };
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
