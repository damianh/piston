// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Piston.Engine.Execution;
using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class OrchestratorResourceWaitTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task WaitingProject_IsShownWaitingForResources_UntilItStarts_AndItsResourcesAreRecorded()
    {
        var projects = new[] { "/w/a.csproj", "/w/b.csproj" };
        var state = new PistonState();
        var runner = new WaitingRunner();
        var observed = new ConcurrentQueue<(ProjectRunStatus? Status, bool Waiting)>();
        state.StateChanged += () => observed.Enqueue((
            state.ProjectStatuses.TryGetValue("/w/a.csproj", out var s) ? s : null,
            state.ProjectResourceWaits.ContainsKey("/w/a.csproj")));
        using var orchestrator = new PistonOrchestrator(
            new StubFileWatcherService(), new StubBuildService(), runner, new StubImpactAnalyzer(projects), state);

        await orchestrator.StartAsync("/w/App.slnx");
        await runner.Completed.WaitAsync(TimeSpan.FromSeconds(30), _ct);
        await WaitForAsync(() => state.LastRun?.Projects.Count == 2);

        Assert.Contains((ProjectRunStatus.WaitingForResources, true), observed);
        Assert.Contains((ProjectRunStatus.Pending, false), observed);
        Assert.Contains((ProjectRunStatus.Running, false), observed);
        Assert.Empty(state.ProjectResourceWaits);
        var outcome = Assert.Single(state.LastRun!.Projects, p => p.ProjectPath == "/w/a.csproj");
        Assert.Equal(["port:5432"], outcome.Resources!.Needs.Keys);
        Assert.Equal(TestResourceWaitState.Acquired, outcome.Resources.Wait!.State);
        Assert.Null(Assert.Single(state.LastRun.Projects, p => p.ProjectPath == "/w/b.csproj").Resources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryWaits_AreReportedAndCleared_OnAcquisitionOrCancellation(bool cancelRetry)
    {
        var state = new PistonState();
        var runner = new WaitingRunner(failFirstAttempt: true, cancelRetry);
        var observed = new ConcurrentQueue<(ProjectRunStatus? Status, bool Waiting)>();
        state.StateChanged += () =>
        {
            if (state.RetryStatus is not null)
            {
                observed.Enqueue((
                    state.ProjectStatuses.TryGetValue("/w/a.csproj", out var status) ? status : null,
                    state.ProjectResourceWaits.ContainsKey("/w/a.csproj")));
            }
        };
        using var orchestrator = new PistonOrchestrator(
            new StubFileWatcherService(), new StubBuildService(), runner, new StubImpactAnalyzer(["/w/a.csproj"]), state)
        {
            RetryFailedTests = 1,
        };

        await orchestrator.StartAsync("/w/App.slnx");
        await WaitForAsync(() => state.LastRun is not null && state.CurrentRun is null);

        Assert.Contains((ProjectRunStatus.WaitingForResources, true), observed);
        Assert.Empty(state.ProjectResourceWaits);
        Assert.Null(state.RetryStatus);
        Assert.DoesNotContain(state.ProjectStatuses.Values, s => s == ProjectRunStatus.WaitingForResources);
        if (cancelRetry)
        {
            Assert.Equal(TestRunStatus.Cancelled, state.LastRun!.Status);
        }
        else
        {
            Assert.Contains((ProjectRunStatus.Pending, false), observed);
            Assert.Contains((ProjectRunStatus.Running, false), observed);
            var test = Assert.Single(state.LastRun!.Suites.SelectMany(s => s.Tests));
            Assert.Equal(TestStatus.Passed, test.Status);
            _ = Assert.Single(test.Attempts);
        }
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20, _ct);
        }
    }

    private sealed class WaitingRunner(bool failFirstAttempt = false, bool cancelRetry = false) : ITestRunnerService
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _completed.Task;

        public Task<TestRunResult> RunTestsAsync(
            string solutionPath, string? filter, Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            Action<IReadOnlyList<TestSuite>>? onProgress, Action<ProjectTestResult>? onProjectCompleted, CancellationToken ct) =>
            throw new NotSupportedException();

        public async Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            TestRunCallbacks callbacks, CancellationToken ct)
        {
            var since = DateTimeOffset.UtcNow;
            var needs = new TestResourceNeeds("/w/a.csproj", ["port:5432"], false, ["declared"]);
            var blocker = new TestResourceBlocker("port:5432", TestResourceBlockerKind.Held, 42, "/other", "/other/a.csproj", since);
            callbacks.OnProjectWaiting?.Invoke(
                "/w/a.csproj", new TestResourceWait(TestResourceWaitState.Waiting, needs.Keys, false, [blocker], 0, 0, null, since, null));
            await Task.Delay(50, ct);
            if (filter is not null && cancelRetry)
            {
                throw new OperationCanceledException(ct);
            }

            var results = new List<ProjectTestResult>();
            foreach (var path in testProjectPaths ?? [])
            {
                callbacks.OnProjectResourcesAcquired?.Invoke(path);
                await Task.Delay(20, ct);
                callbacks.OnProjectStarted?.Invoke(path);
                await Task.Delay(20, ct);
                var name = Path.GetFileNameWithoutExtension(path);
                var suite = new TestSuite(name,
                    [new TestResult(name + ".T", "T",
                        failFirstAttempt && filter is null ? TestStatus.Failed : TestStatus.Passed,
                        TimeSpan.Zero, null, null, null, null)],
                    DateTimeOffset.UtcNow, TimeSpan.Zero);
                var result = new ProjectTestResult(path, [suite], null, [], Crashed: false);
                if (path == "/w/a.csproj")
                {
                    result = result with
                    {
                        Resources = new TestProjectResources(needs,
                            new TestResourceWait(TestResourceWaitState.Acquired, needs.Keys, false, [], 0, 0, null, since, DateTimeOffset.UtcNow),
                            []),
                    };
                }

                callbacks.OnProjectCompleted?.Invoke(result);
                results.Add(result);
            }

            _ = _completed.TrySetResult();
            return new TestRunResult([.. results.SelectMany(r => r.Suites)], null, []) { Projects = results };
        }

        public Task DiscoverTestsAsync(
            IReadOnlyList<string> testProjectPaths, string? filter,
            Action<string, IReadOnlyList<TestResult>> onDiscovered, CancellationToken ct) => Task.CompletedTask;
    }
}
