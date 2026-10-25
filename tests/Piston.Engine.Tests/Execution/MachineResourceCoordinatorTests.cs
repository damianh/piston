// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Piston.Engine.Execution;
using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.Execution;

public sealed class MachineResourceCoordinatorTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-test-resources-{Guid.NewGuid():N}");
    private readonly ExecutionCoordinationDirectory _directory;

    public MachineResourceCoordinatorTests()
    {
        _directory = new ExecutionCoordinationDirectory(_root);
        _directory.EnsureReady();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private MachineResourceCoordinator Coordinator(string worktree = "/wt/a", Func<int, bool>? isPortInUse = null) =>
        new(_directory, TestResourceNeeds.None, worktree, Poll, isPortInUse ?? (_ => false));

    private static TestResourceNeeds Needs(string project, bool containers = false, params string[] keys) =>
        new(project, keys, containers, []);

    private sealed class WaitLog
    {
        private readonly ConcurrentQueue<TestResourceWait> _items = new();
        public void Add(TestResourceWait status) => _items.Enqueue(status);
        public TestResourceWait? Last => _items.LastOrDefault();
        public IReadOnlyList<TestResourceWait> All => [.. _items];
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(10);
        }
    }

    private int Files(string directory) => Directory.GetFiles(directory).Length;

    private void SetBudget(string json) => File.WriteAllText(_directory.BudgetPath, json);

    [Fact]
    public async Task EmptyNeeds_AreNotCoordinated()
    {
        using var lease = await Coordinator().AcquireAsync(Needs("/wt/a/A.csproj"), _ => { }, _ct);

        Assert.Null(lease.Status);
        Assert.Equal(0, Files(_directory.ResourceLeasesDirectory));
        Assert.Equal(0, Files(_directory.ResourceQueueDirectory));
    }

    [Fact]
    public async Task FreeResources_AreAcquiredImmediately_AndReleasedOnDispose()
    {
        var log = new WaitLog();

        var lease = await Coordinator().AcquireAsync(Needs("/wt/a/A.csproj", false, "port:5432"), log.Add, _ct);

        Assert.Empty(log.All);
        var status = lease.Status!;
        Assert.Equal(TestResourceWaitState.Acquired, status.State);
        Assert.Equal(["port:5432"], status.Keys);
        Assert.Empty(status.BlockedBy);
        _ = Assert.NotNull(status.AcquiredAt);
        Assert.Equal(2, Files(_directory.ResourceLeasesDirectory)); // lease and sidecar
        Assert.Equal(0, Files(_directory.ResourceQueueDirectory));

        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, Files(_directory.ResourceLeasesDirectory));
    }

    [Fact]
    public async Task SharedKey_Waits_ReportingTheHolder_UntilReleased()
    {
        var holder = await Coordinator("/wt/a").AcquireAsync(Needs("/wt/a/Db.Tests.csproj", true, "port:5432"), _ => { }, _ct);
        var log = new WaitLog();
        try
        {
            var waiting = Coordinator("/wt/b").AcquireAsync(Needs("/wt/b/Db.Tests.csproj", true, "port:5432", "container:db"), log.Add, _ct);
            await WaitForAsync(() => log.Last is not null, "the second run did not report waiting");
            await Task.Delay(100, _ct);
            Assert.False(waiting.IsCompleted);

            var wait = log.Last!;
            Assert.Equal(TestResourceWaitState.Waiting, wait.State);
            var blocker = Assert.Single(wait.BlockedBy);
            Assert.Equal(("port:5432", TestResourceBlockerKind.Held), (blocker.Resource, blocker.Kind));
            Assert.Equal(("/wt/a", "/wt/a/Db.Tests.csproj", Environment.ProcessId), (blocker.Worktree, blocker.Project, blocker.ProcessId));
            Assert.Equal(1, wait.ContainerSlotsInUse);
            Assert.Null(wait.ContainerSlotBudget);
            _ = Assert.Single(log.All); // unchanged status is not republished

            holder.Dispose();
            using var acquired = await waiting.WaitAsync(Timeout, _ct);
            Assert.Equal(TestResourceWaitState.Acquired, acquired.Status!.State);
            Assert.True(acquired.Status.Waited > TimeSpan.Zero);
        }
        finally
        {
            holder.Dispose();
        }
    }

    [Fact]
    public async Task DisjointKeys_RunConcurrently()
    {
        using var a = await Coordinator().AcquireAsync(Needs("/wt/a/A.csproj", false, "port:1"), _ => { }, _ct);
        using var b = await Coordinator().AcquireAsync(Needs("/wt/a/B.csproj", false, "port:2"), _ => { }, _ct)
            .WaitAsync(Timeout, _ct);

        Assert.Equal(TestResourceWaitState.Acquired, b.Status!.State);
    }

    [Fact]
    public async Task EarlierQueuedRun_GoesFirst_EvenWhenALaterRunsResourcesAreFree()
    {
        var holder = await Coordinator().AcquireAsync(Needs("/wt/a/A.csproj", false, "resource:one"), _ => { }, _ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        try
        {
            var firstLog = new WaitLog();
            var first = Coordinator().AcquireAsync(Needs("/wt/a/B.csproj", false, "resource:one", "resource:two"), firstLog.Add, cancel.Token);
            await WaitForAsync(() => firstLog.Last is not null, "the first queued run did not wait");

            var secondLog = new WaitLog();
            var second = Coordinator().AcquireAsync(Needs("/wt/a/C.csproj", false, "resource:two"), secondLog.Add, cancel.Token);
            await WaitForAsync(() => secondLog.Last is not null, "the second queued run did not wait");

            var blocker = Assert.Single(secondLog.Last!.BlockedBy);
            Assert.Equal(("resource:two", TestResourceBlockerKind.Queued, "/wt/a/B.csproj"), (blocker.Resource, blocker.Kind, blocker.Project));
            Assert.Equal(1, secondLog.Last.QueuedAhead);

            holder.Dispose();
            var firstLease = await first.WaitAsync(Timeout, _ct);
            await Task.Delay(100, _ct);
            Assert.False(second.IsCompleted);

            firstLease.Dispose();
            using var secondLease = await second.WaitAsync(Timeout, _ct);
            Assert.Equal(TestResourceWaitState.Acquired, secondLease.Status!.State);
        }
        finally
        {
            await cancel.CancelAsync();
            holder.Dispose();
        }
    }

    [Fact]
    public async Task ContainerSlots_LimitConcurrentContainerRuns_ButNotOtherRuns()
    {
        SetBudget("""{ "cpuSlots": 4, "containerSlots": 1 }""");
        var holder = await Coordinator().AcquireAsync(Needs("/wt/a/A.csproj", true, "project:r/A.csproj"), _ => { }, _ct);
        try
        {
            Assert.Equal((1, 1), (holder.Status!.ContainerSlotsInUse, holder.Status.ContainerSlotBudget));
            var log = new WaitLog();
            var waiting = Coordinator().AcquireAsync(Needs("/wt/a/B.csproj", true, "project:r/B.csproj"), log.Add, _ct);
            await WaitForAsync(() => log.Last is not null, "the container run did not wait for a slot");

            var blocker = Assert.Single(log.Last!.BlockedBy);
            Assert.Equal((TestResourceBlocker.ContainerSlots, TestResourceBlockerKind.Held), (blocker.Resource, blocker.Kind));
            Assert.Equal((1, 1), (log.Last.ContainerSlotsInUse, log.Last.ContainerSlotBudget));

            using (var unrelated = await Coordinator().AcquireAsync(Needs("/wt/a/C.csproj", false, "port:9"), _ => { }, _ct).WaitAsync(Timeout, _ct))
            {
                Assert.Equal(TestResourceWaitState.Acquired, unrelated.Status!.State);
            }

            holder.Dispose();
            using var acquired = await waiting.WaitAsync(Timeout, _ct);
            Assert.Equal(TestResourceWaitState.Acquired, acquired.Status!.State);
        }
        finally
        {
            holder.Dispose();
        }
    }

    [Fact]
    public async Task Cancellation_WhileWaiting_RemovesTheTicket()
    {
        using var holder = await Coordinator().AcquireAsync(Needs("/wt/a/A.csproj", false, "port:1"), _ => { }, _ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var log = new WaitLog();
        var waiting = Coordinator().AcquireAsync(Needs("/wt/a/B.csproj", false, "port:1"), log.Add, cancel.Token);
        await WaitForAsync(() => log.Last is not null, "the run did not wait");
        Assert.Equal(2, Files(_directory.ResourceQueueDirectory));

        await cancel.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Timeout, _ct));
        Assert.Equal(0, Files(_directory.ResourceQueueDirectory));
    }

    [Fact]
    public async Task CrashedHolder_IsRecovered_AndItsEntriesRemoved()
    {
        using var holder = LeaseHolderProcess.Start("resource", _root, "port:4444", "1");
        Assert.Equal("READY", holder.WaitForLine("READY", "ERROR"));
        var log = new WaitLog();
        var waiting = Coordinator().AcquireAsync(Needs("/wt/a/A.csproj", false, "port:4444"), log.Add, _ct);
        await WaitForAsync(() => log.Last is not null, "the run did not wait for the other process");
        var blocker = Assert.Single(log.Last!.BlockedBy);
        Assert.Equal((holder.ProcessId, "holder-worktree", "Holder.csproj"), (blocker.ProcessId, blocker.Worktree, blocker.Project));

        holder.Kill();

        using var lease = await waiting.WaitAsync(Timeout, _ct);
        Assert.Equal(TestResourceWaitState.Acquired, lease.Status!.State);
        Assert.DoesNotContain(Directory.GetFiles(_directory.ResourceLeasesDirectory),
            f => Path.GetFileName(f).Contains($"_{holder.ProcessId}_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnotherProcess_WaitsForThisProcess()
    {
        var lease = await Coordinator().AcquireAsync(Needs("/wt/a/A.csproj", false, "container:db"), _ => { }, _ct);
        using var holder = LeaseHolderProcess.Start("resource", _root, "container:db", "0");
        try
        {
            Assert.StartsWith("WAITING 1", holder.WaitForLine("WAITING", "READY", "ERROR"), StringComparison.Ordinal);
        }
        finally
        {
            lease.Dispose();
        }

        Assert.Equal("READY", holder.WaitForLine("READY", "ERROR"));
    }

    [Fact]
    public async Task OrphanSidecars_AreRemoved()
    {
        var orphan = Path.Combine(_directory.ResourceQueueDirectory, "00000000000000000001_1_dead.json");
        File.WriteAllText(orphan, "{}");

        using var lease = await Coordinator().AcquireAsync(Needs("/wt/a/A.csproj", false, "port:1"), _ => { }, _ct);

        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task DeclaredPortInUseOutsidePiston_IsAWarning()
    {
        using var lease = await Coordinator(isPortInUse: port => port == 5432)
            .AcquireAsync(Needs("/wt/a/A.csproj", false, "port:5432", "port:6000", "container:db"), _ => { }, _ct);

        var warning = Assert.Single(lease.Warnings);
        Assert.Contains("5432", warning, StringComparison.Ordinal);
        Assert.Equal(lease.Warnings, lease.Resources.Warnings);
    }

    [Fact]
    public void IsPortInUse_DetectsABoundListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.True(MachineResourceCoordinator.IsPortInUse(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void ContainerSlots_InBudget_MustBePositive()
    {
        SetBudget("""{ "cpuSlots": 4, "containerSlots": 2 }""");
        Assert.Equal(2, ExecutionBudget.Load(_directory.BudgetPath).ContainerSlots);

        SetBudget("""{ "cpuSlots": 4 }""");
        Assert.Null(ExecutionBudget.Load(_directory.BudgetPath).ContainerSlots);

        SetBudget("""{ "cpuSlots": 4, "containerSlots": 0 }""");
        var ex = Assert.Throws<ExecutionCoordinationException>(() => ExecutionBudget.Load(_directory.BudgetPath));
        Assert.Contains("containerSlots", ex.Message, StringComparison.Ordinal);
    }
}
