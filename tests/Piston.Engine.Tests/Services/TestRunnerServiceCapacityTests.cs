// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TestRunnerServiceCapacityTests
{
    [Fact]
    public async Task DirectSolutionRun_ReportsProjectCompleted()
    {
        var strategy = new GatedStrategy();
        strategy.ReleaseDiscovery();
        var sut = new TestRunnerService(strategy);
        var completed = new List<ProjectTestResult>();

        var result = await sut.RunTestsAsync(
            "/w/App.slnx",
            testProjectPaths: null,
            filter: null,
            collectCoverage: false,
            new TestRunCallbacks { OnProjectCompleted = completed.Add },
            CancellationToken.None);

        var single = Assert.Single(completed);
        Assert.Equal("/w/App.slnx", single.ProjectPath);
        _ = Assert.Single(result.Suites);
    }

    [Fact]
    public async Task Discovery_SharesPoolCapacityWithExecution()
    {
        var strategy = new GatedStrategy();
        using var pool = new TestProcessPool(poolSize: 1, recycleAfter: 50, strategy);
        var sut = new TestRunnerService(strategy, pool);

        await AssertExecutionWaitsForDiscoveryAsync(sut, strategy);
    }

    [Fact]
    public async Task Discovery_SharesSingleSlotWithExecution_WithoutPool()
    {
        var strategy = new GatedStrategy();
        var sut = new TestRunnerService(strategy);

        await AssertExecutionWaitsForDiscoveryAsync(sut, strategy);
    }

    private static async Task AssertExecutionWaitsForDiscoveryAsync(TestRunnerService sut, GatedStrategy strategy)
    {
        var discovery = sut.DiscoverTestsAsync(["/w/A.csproj"], null, (_, _) => { }, CancellationToken.None);
        await strategy.DiscoveryStarted.WaitAsync(TimeSpan.FromSeconds(10));

        var run = sut.RunTestsAsync(
            "/w/App.slnx",
            ["/w/B.csproj"],
            filter: null,
            collectCoverage: false,
            new TestRunCallbacks(),
            CancellationToken.None);

        var early = await Task.WhenAny(strategy.ExecutionStarted, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(strategy.ExecutionStarted, early);

        strategy.ReleaseDiscovery();
        await discovery.WaitAsync(TimeSpan.FromSeconds(10));
        _ = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, strategy.MaxConcurrency);
    }

    private sealed class GatedStrategy : ITestExecutionStrategy
    {
        private readonly TaskCompletionSource _discoveryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _discoveryGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _executionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _maxConcurrency;

        public Task DiscoveryStarted => _discoveryStarted.Task;

        public Task ExecutionStarted => _executionStarted.Task;

        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public void ReleaseDiscovery() => _discoveryGate.TrySetResult();

        public bool CanExecute(string projectPath) => true;

        public async Task<ProjectTestResult> ExecuteAsync(
            ProjectTestRequest request,
            Action<IReadOnlyList<TestSuite>>? onProgress,
            CancellationToken ct)
        {
            Enter();
            try
            {
                _ = _executionStarted.TrySetResult();
                await Task.Yield();
                var suite = new TestSuite(request.ProjectPath, [], DateTimeOffset.UtcNow, TimeSpan.Zero);
                return new ProjectTestResult(request.ProjectPath, [suite], null, [], Crashed: false);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _active);
            }
        }

        public async Task<IReadOnlyList<TestResult>> DiscoverAsync(
            string projectPath,
            string? filter,
            Action<IReadOnlyList<TestResult>>? onPartial,
            CancellationToken ct)
        {
            Enter();
            try
            {
                _ = _discoveryStarted.TrySetResult();
                await _discoveryGate.Task.WaitAsync(ct);
                return [];
            }
            finally
            {
                _ = Interlocked.Decrement(ref _active);
            }
        }

        private void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            int seen;
            while (active > (seen = Volatile.Read(ref _maxConcurrency))
                && Interlocked.CompareExchange(ref _maxConcurrency, active, seen) != seen)
            {
            }
        }
    }
}
