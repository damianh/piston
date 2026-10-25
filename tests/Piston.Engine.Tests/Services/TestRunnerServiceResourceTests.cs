// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Piston.Engine.Execution;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TestRunnerServiceResourceTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private readonly ConcurrentQueue<string> _events = new();

    public static TheoryData<string> Paths => ["solution", "sequential", "pool"];

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task EachRun_HoldsItsResources_FromBeforeItStarts_UntilBeforeItCompletes(string path)
    {
        var gate = new FakeGate(_events);
        var (sut, targets, pool) = Create(path, gate);
        using var poolScope = pool;
        var completed = new ConcurrentQueue<ProjectTestResult>();

        var result = await RunAsync(sut, path, targets, new TestRunCallbacks
        {
            OnProjectStarted = p => _events.Enqueue($"started {Name(p)}"),
            OnProjectCompleted = r =>
            {
                _events.Enqueue($"completed {Name(r.ProjectPath)}");
                completed.Enqueue(r);
            },
        });

        var events = _events.ToList();
        foreach (var target in targets)
        {
            var name = Name(target);
            int At(string e) => events.IndexOf($"{e} {name}");
            Assert.True(At("acquire") >= 0, $"{name} acquired no resources: {string.Join(", ", events)}");
            Assert.True(At("acquire") < At("execute"), string.Join(", ", events));
            Assert.True(At("execute") < At("release"), string.Join(", ", events));
            Assert.True(At("release") < At("completed"), string.Join(", ", events));
        }

        Assert.Equal(targets.Order(), gate.Resolved.Order());
        Assert.Equal(targets.Length, result.Projects.Count);
        Assert.All(completed, r =>
        {
            Assert.NotNull(r.Resources);
            Assert.Equal(["port:1"], r.Resources.Needs.Keys);
            Assert.Equal(TestResourceWaitState.Acquired, r.Resources.Wait!.State);
        });
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Waiting_IsReportedPerProject(string path)
    {
        var gate = new FakeGate(_events) { WaitBeforeAcquiring = true };
        var (sut, targets, pool) = Create(path, gate);
        using var poolScope = pool;
        var waits = new ConcurrentQueue<(string Project, TestResourceWait Wait)>();

        _ = await RunAsync(sut, path, targets, new TestRunCallbacks
        {
            OnProjectWaiting = (p, w) => waits.Enqueue((p, w)),
        });

        Assert.Equal(targets.Order(), waits.Select(w => w.Project).Order());
        Assert.All(waits, w => Assert.Equal(TestResourceWaitState.Waiting, w.Wait.State));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Acquisition_IsReportedBeforeEnteringAFullPool(string path)
    {
        var gate = new FakeGate(_events) { WaitBeforeAcquiring = true };
        var strategy = new RecordingStrategy(_events);
        using var pool = new TestProcessPool(poolSize: 1, recycleAfter: 50, strategy);
        using var occupied = await pool.AcquireSlotAsync(_ct);
        var sut = new TestRunnerService(strategy, pool, gate);
        var targets = path switch
        {
            "solution" => new[] { "/w/App.slnx" },
            "sequential" => ["/w/A.csproj"],
            _ => ["/w/A.csproj", "/w/B.csproj"],
        };
        var acquired = new ConcurrentQueue<string>();
        var run = RunAsync(sut, path, targets, new TestRunCallbacks
        {
            OnProjectWaiting = (p, _) => _events.Enqueue($"waiting {Name(p)}"),
            OnProjectResourcesAcquired = p => acquired.Enqueue(p),
            OnProjectStarted = p => _events.Enqueue($"started {Name(p)}"),
        });

        await gate.Acquired.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        // The gate returns before the callback, so allow all projects to reach the pool queue.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (acquired.Count != targets.Length)
        {
            Assert.True(DateTime.UtcNow < deadline, "Acquisition was not published while the pool was full.");
            await Task.Delay(10, _ct);
        }
        Assert.False(run.IsCompleted);
        Assert.DoesNotContain(_events, e => e.StartsWith("started ", StringComparison.Ordinal));
        Assert.DoesNotContain(_events, e => e.StartsWith("execute ", StringComparison.Ordinal));
        Assert.Equal(targets.Order(), acquired.Order());

        occupied.Dispose();
        _ = await run.WaitAsync(TimeSpan.FromSeconds(10), _ct);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task UncoordinatedRuns_HaveNoResources(string path)
    {
        var gate = new FakeGate(_events) { Uncoordinated = true };
        var (sut, targets, pool) = Create(path, gate);
        using var poolScope = pool;

        var result = await RunAsync(sut, path, targets, new TestRunCallbacks());

        Assert.Equal(targets.Length, result.Projects.Count);
        Assert.All(result.Projects, r => Assert.Null(r.Resources));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task CoordinationFailure_CrashesTheProject_WithoutRunningIt(string path)
    {
        var gate = new FakeGate(_events) { Failure = new ExecutionCoordinationException("budget.json is invalid") };
        var (sut, targets, pool) = Create(path, gate);
        using var poolScope = pool;
        var completed = new ConcurrentQueue<ProjectTestResult>();

        var result = await RunAsync(sut, path, targets, new TestRunCallbacks { OnProjectCompleted = completed.Enqueue });

        Assert.DoesNotContain(_events, e => e.StartsWith("execute", StringComparison.Ordinal));
        Assert.Equal(targets.Length, completed.Count);
        Assert.All(result.Projects, r =>
        {
            Assert.True(r.Crashed);
            Assert.Contains("budget.json is invalid", r.RunnerError, StringComparison.Ordinal);
        });
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task CancellationWhileWaiting_RunsNothing(string path)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var gate = new FakeGate(_events) { BlockUntilCancelled = true };
        var (sut, targets, pool) = Create(path, gate);
        using var poolScope = pool;
        var completed = new ConcurrentQueue<ProjectTestResult>();

        var run = RunAsync(sut, path, targets, new TestRunCallbacks { OnProjectCompleted = completed.Enqueue }, cancel.Token);
        await gate.Blocked.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        await cancel.CancelAsync();

        TestRunResult? result = null;
        try
        {
            result = await run.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.DoesNotContain(_events, e => e.StartsWith("execute", StringComparison.Ordinal));
        Assert.All(completed, r => Assert.Empty(r.Suites));
        Assert.All(completed, r => Assert.False(r.Crashed));
        Assert.All(result?.Projects ?? [], r => Assert.Null(r.RunnerError));
    }

    private (TestRunnerService Sut, string[] Targets, IDisposable? Pool) Create(string path, FakeGate gate)
    {
        var strategy = new RecordingStrategy(_events);
        return path switch
        {
            "solution" => (new TestRunnerService(strategy, null, gate), ["/w/App.slnx"], null),
            "sequential" => (new TestRunnerService(strategy, null, gate), ["/w/A.csproj", "/w/B.csproj"], null),
            _ => CreatePooled(strategy, gate),
        };
    }

    private static (TestRunnerService, string[], IDisposable?) CreatePooled(RecordingStrategy strategy, FakeGate gate)
    {
        var pool = new TestProcessPool(poolSize: 2, recycleAfter: 50, strategy);
        return (new TestRunnerService(strategy, pool, gate), ["/w/A.csproj", "/w/B.csproj"], pool);
    }

    private Task<TestRunResult> RunAsync(
        TestRunnerService sut, string path, string[] targets, TestRunCallbacks callbacks, CancellationToken? ct = null) =>
        sut.RunTestsAsync(
            "/w/App.slnx",
            path == "solution" ? null : targets,
            filter: null,
            collectCoverage: false,
            callbacks,
            ct ?? _ct);

    private static string Name(string path) => Path.GetFileNameWithoutExtension(path);

    private sealed class FakeGate(ConcurrentQueue<string> events) : ITestResourceGate
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Resolved { get; } = new();

        public bool WaitBeforeAcquiring { get; init; }

        public bool Uncoordinated { get; init; }

        public bool BlockUntilCancelled { get; init; }

        public Exception? Failure { get; init; }

        public Task Blocked => _blocked.Task;
        public Task Acquired => _acquired.Task;

        public TestResourceNeeds Resolve(string projectPath)
        {
            Resolved.Enqueue(projectPath);
            return new TestResourceNeeds(projectPath, ["port:1"], false, ["declared"]);
        }

        public async Task<TestResourceLease> AcquireAsync(
            TestResourceNeeds needs, Action<TestResourceWait> onWaiting, CancellationToken ct)
        {
            await Task.Yield();
            if (Failure is not null)
            {
                throw Failure;
            }

            var since = DateTimeOffset.UtcNow;
            if (WaitBeforeAcquiring || BlockUntilCancelled)
            {
                onWaiting(Status(needs, TestResourceWaitState.Waiting, since, null));
            }

            if (BlockUntilCancelled)
            {
                _ = _blocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (Uncoordinated)
            {
                return TestResourceLease.Uncoordinated(needs);
            }

            var name = Name(needs.ProjectPath);
            events.Enqueue($"acquire {name}");
            _ = _acquired.TrySetResult();
            return new TestResourceLease(
                needs,
                Status(needs, TestResourceWaitState.Acquired, since, DateTimeOffset.UtcNow),
                [],
                () => events.Enqueue($"release {name}"));
        }

        private static TestResourceWait Status(
            TestResourceNeeds needs, TestResourceWaitState state, DateTimeOffset since, DateTimeOffset? acquiredAt) =>
            new(state, needs.Keys, needs.UsesContainers, [], 0, 0, null, since, acquiredAt);
    }

    private sealed class RecordingStrategy(ConcurrentQueue<string> events) : ITestExecutionStrategy
    {
        public bool CanExecute(string projectPath) => true;

        public async Task<ProjectTestResult> ExecuteAsync(
            ProjectTestRequest request, Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct)
        {
            events.Enqueue($"execute {Name(request.ProjectPath)}");
            await Task.Yield();
            var suite = new TestSuite(request.ProjectPath, [], DateTimeOffset.UtcNow, TimeSpan.Zero);
            return new ProjectTestResult(request.ProjectPath, [suite], null, [], Crashed: false);
        }

        public Task<IReadOnlyList<TestResult>> DiscoverAsync(
            string projectPath, string? filter, Action<IReadOnlyList<TestResult>>? onPartial, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TestResult>>([]);
    }
}
