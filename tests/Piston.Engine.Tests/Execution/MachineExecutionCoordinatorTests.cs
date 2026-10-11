// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Piston.Engine.Execution;
using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.Execution;

public sealed class MachineExecutionCoordinatorTests : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-admission-{Guid.NewGuid():N}");
    private readonly ExecutionCoordinationDirectory _directory;

    public MachineExecutionCoordinatorTests()
    {
        _directory = new ExecutionCoordinationDirectory(_root);
        _directory.EnsureReady();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void SetBudget(string json) => File.WriteAllText(_directory.BudgetPath, json);

    private MachineExecutionCoordinator Coordinator(int cpu, int? memoryMb = null)
    {
        var coordinator = new MachineExecutionCoordinator(_directory, new ExecutionReservation(cpu, memoryMb), Poll);
        _ = coordinator.Initialize();
        return coordinator;
    }

    private sealed class StatusLog
    {
        private readonly ConcurrentQueue<TestRunAdmission> _items = new();
        public void Add(TestRunAdmission status) => _items.Enqueue(status);
        public TestRunAdmission? Last => _items.LastOrDefault();
        public IReadOnlyList<TestRunAdmission> All => _items.ToList();
    }

    private static Task<IExecutionLease> Admit(
        MachineExecutionCoordinator coordinator, StatusLog? log = null, long? inherited = null, CancellationToken ct = default) =>
        coordinator.AdmitAsync(Guid.NewGuid().ToString("N"), s => log?.Add(s), inherited, ct);

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(10);
        }
    }

    private int EntryCount(string directory) => Directory.GetFiles(directory).Length;

    [Fact]
    public async Task CpuBudget_IntMaxReservation_DoesNotOverflowAdmissionComparison()
    {
        SetBudget("""{ "cpuSlots": 2147483647 }""");
        var holder = await Admit(Coordinator(int.MaxValue));
        using var cancellation = new CancellationTokenSource();
        var log = new StatusLog();
        var waiting = Admit(Coordinator(1), log, ct: cancellation.Token);
        try
        {
            await WaitForAsync(() => log.Last is not null || waiting.IsCompleted, "second request did not enter admission");
            Assert.False(waiting.IsCompleted);
            Assert.Equal(int.MaxValue, log.Last!.CpuSlotsInUse);
            holder.Dispose();
            using var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(TestRunAdmissionState.Admitted, admitted.Status!.State);
        }
        finally
        {
            cancellation.Cancel();
            holder.Dispose();
        }
    }

    [Fact]
    public async Task Admit_WhenBudgetIsFree_IsImmediate_AndReportsReservation()
    {
        SetBudget("""{ "cpuSlots": 4, "memoryMb": 2048 }""");
        var log = new StatusLog();

        using var lease = await Admit(Coordinator(2, 512), log);

        Assert.Empty(log.All);
        var status = lease.Status!;
        Assert.Equal(TestRunAdmissionState.Admitted, status.State);
        Assert.Equal((2, 512, 4, 2048), (status.ReservedCpuSlots, status.ReservedMemoryMb!.Value, status.BudgetCpuSlots, status.BudgetMemoryMb!.Value));
        Assert.Equal(_directory.BudgetPath, status.BudgetSource);
        Assert.Equal((0, 0, 0, 0L), (status.QueuedAhead, status.OtherAdmittedRuns, status.CpuSlotsInUse, status.MemoryMbInUse));
        _ = Assert.NotNull(status.AdmittedAt);
        Assert.Equal(1, EntryCount(_directory.LeasesDirectory));
        Assert.Equal(0, EntryCount(_directory.QueueDirectory));

        lease.Dispose();
        Assert.Equal(0, EntryCount(_directory.LeasesDirectory));
    }

    [Fact]
    public async Task Admit_BeyondCpuBudget_QueuesVisibly_UntilASlotIsReleased()
    {
        SetBudget("""{ "cpuSlots": 2 }""");
        var first = await Admit(Coordinator(1));
        using var second = await Admit(Coordinator(1));
        var log = new StatusLog();

        var third = Admit(Coordinator(1), log);
        await WaitForAsync(() => log.Last is not null, "third run was not reported as queued");

        Assert.False(third.IsCompleted);
        var queued = log.Last!;
        Assert.Equal(TestRunAdmissionState.Queued, queued.State);
        Assert.Equal((0, 1, 2, 2), (queued.QueuedAhead, queued.QueueLength, queued.OtherAdmittedRuns, queued.CpuSlotsInUse));
        Assert.Null(queued.AdmittedAt);
        Assert.Equal(1, EntryCount(_directory.QueueDirectory));

        first.Dispose();
        using var admitted = await third.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(TestRunAdmissionState.Admitted, admitted.Status!.State);
        Assert.Equal(queued.QueuedAt, admitted.Status.QueuedAt);
        Assert.Equal(0, EntryCount(_directory.QueueDirectory));
    }

    [Fact]
    public async Task Admit_BeyondMemoryBudget_Queues()
    {
        SetBudget("""{ "cpuSlots": 8, "memoryMb": 1000 }""");
        var first = await Admit(Coordinator(1, 600));
        var log = new StatusLog();

        var second = Admit(Coordinator(1, 600), log);
        await WaitForAsync(() => log.Last is not null, "second run was not queued");
        Assert.Equal(600, log.Last!.MemoryMbInUse);

        first.Dispose();
        using var lease = await second.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Admission_IsStrictFifo_SmallerRunsDoNotOvertakeTheHead()
    {
        SetBudget("""{ "cpuSlots": 4 }""");
        var large = await Admit(Coordinator(3));
        var headLog = new StatusLog();
        var head = Admit(Coordinator(2), headLog);
        await WaitForAsync(() => headLog.Last is not null, "head was not queued");

        var tailLog = new StatusLog();
        var tail = Admit(Coordinator(1), tailLog);
        await WaitForAsync(() => tailLog.Last is not null, "tail was not queued");

        // The tail would fit next to the large run, but it must not starve the head.
        await Task.Delay(100);
        Assert.False(tail.IsCompleted);
        Assert.Equal((1, 2), (tailLog.Last!.QueuedAhead, tailLog.Last.QueueLength));

        large.Dispose();
        using var headLease = await head.WaitAsync(TimeSpan.FromSeconds(15));
        using var tailLease = await tail.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, tailLease.Status!.CpuSlotsInUse);
    }

    [Fact]
    public async Task ConcurrentAdmissions_NeverExceedTheBudget()
    {
        SetBudget("""{ "cpuSlots": 3 }""");
        var running = 0;
        var maxRunning = 0;
        var maxReported = 0;

        async Task RunOnce(int index)
        {
            var coordinator = Coordinator(1);
            using var lease = await Admit(coordinator);
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            InterlockedMax(ref maxReported, lease.Status!.CpuSlotsInUse + 1);
            await Task.Delay(5 + index % 4 * 5);
            _ = Interlocked.Decrement(ref running);
        }

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => RunOnce(i)))).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.InRange(maxRunning, 1, 3);
        Assert.InRange(maxReported, 1, 3);
        Assert.Equal(0, EntryCount(_directory.LeasesDirectory));
        Assert.Equal(0, EntryCount(_directory.QueueDirectory));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    [Fact]
    public async Task Cancellation_WhileQueued_RemovesTheTicket_AndReturnsItsPosition()
    {
        SetBudget("""{ "cpuSlots": 1 }""");
        using var holder = await Admit(Coordinator(1));
        using var cts = new CancellationTokenSource();
        var log = new StatusLog();

        var queued = Admit(Coordinator(1), log, ct: cts.Token);
        await WaitForAsync(() => log.Last is not null, "run was not queued");
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<ExecutionAdmissionCancelledException>(() => queued);
        _ = Assert.NotNull(ex.QueueSequence);
        Assert.Equal(0, EntryCount(_directory.QueueDirectory));
    }

    [Fact]
    public async Task InheritedSequence_KeepsQueuePosition_AheadOfLaterArrivals()
    {
        SetBudget("""{ "cpuSlots": 1 }""");
        var holder = await Admit(Coordinator(1));
        var superseded = Coordinator(1);
        using var cts = new CancellationTokenSource();
        var firstLog = new StatusLog();
        var first = Admit(superseded, firstLog, ct: cts.Token);
        await WaitForAsync(() => firstLog.Last is not null, "first was not queued");

        var laterLog = new StatusLog();
        var later = Admit(Coordinator(1), laterLog);
        await WaitForAsync(() => laterLog.Last is { QueuedAhead: 1 }, "later did not queue behind first");

        cts.Cancel();
        var cancelled = await Assert.ThrowsAsync<ExecutionAdmissionCancelledException>(() => first);

        var replacementLog = new StatusLog();
        var replacement = Admit(superseded, replacementLog, cancelled.QueueSequence);
        await WaitForAsync(() => replacementLog.Last is not null && laterLog.Last is { QueuedAhead: 1, QueueLength: 2 },
            "replacement did not rejoin");
        Assert.Equal(0, replacementLog.Last!.QueuedAhead);
        Assert.Equal(firstLog.Last!.QueuedAt, replacementLog.Last.QueuedAt);

        holder.Dispose();
        using var replacementLease = await replacement.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(100);
        Assert.False(later.IsCompleted);
        replacementLease.Dispose();
        using var laterLease = await later.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task KilledProcessLease_IsReclaimed()
    {
        SetBudget("""{ "cpuSlots": 2 }""");
        using var a = LeaseHolderProcess.Start("admit", _directory.BaseDirectory, "1", "0");
        using var b = LeaseHolderProcess.Start("admit", _directory.BaseDirectory, "1", "0");
        Assert.Equal("READY", a.WaitForLine("READY", "ERROR"));
        Assert.Equal("READY", b.WaitForLine("READY", "ERROR"));

        var log = new StatusLog();
        var queued = Admit(Coordinator(1), log);
        await WaitForAsync(() => log.Last is not null, "run was not queued behind other processes");
        Assert.Equal(2, log.Last!.OtherAdmittedRuns);

        a.Kill();

        using var lease = await queued.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, lease.Status!.OtherAdmittedRuns);
    }

    [Fact]
    public async Task KilledProcessTicket_DoesNotBlockTheQueue()
    {
        SetBudget("""{ "cpuSlots": 1 }""");
        var holder = await Admit(Coordinator(1));
        using var waiting = LeaseHolderProcess.Start("admit", _directory.BaseDirectory, "1", "0");
        Assert.StartsWith("QUEUED 0", waiting.WaitForLine("QUEUED", "READY", "ERROR"), StringComparison.Ordinal);

        var log = new StatusLog();
        var mine = Admit(Coordinator(1), log);
        await WaitForAsync(() => log.Last is { QueuedAhead: 1 }, "run did not queue behind the other process");

        waiting.Kill();
        await WaitForAsync(() => log.Last is { QueuedAhead: 0 }, "killed ticket was not purged");

        holder.Dispose();
        using var lease = await mine.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task QueuedInAnotherProcess_IsAdmittedWhenSlotIsReleased()
    {
        SetBudget("""{ "cpuSlots": 1 }""");
        var holder = await Admit(Coordinator(1));
        using var waiting = LeaseHolderProcess.Start("admit", _directory.BaseDirectory, "1", "0");
        Assert.StartsWith("QUEUED", waiting.WaitForLine("QUEUED", "READY", "ERROR"), StringComparison.Ordinal);

        holder.Dispose();

        Assert.Equal("READY", waiting.WaitForLine("READY", "ERROR"));
    }

    [Fact]
    public async Task UnheldLeftoverEntries_AreRemoved()
    {
        SetBudget("""{ "cpuSlots": 1 }""");
        File.WriteAllText(Path.Combine(_directory.LeasesDirectory, "00000000000000000001_1_0_1_dead.lease"), "");
        File.WriteAllText(Path.Combine(_directory.QueueDirectory, "00000000000000000001_1_0_1_dead.ticket"), "");
        File.WriteAllText(Path.Combine(_directory.QueueDirectory, "garbage"), "");

        using var lease = await Admit(Coordinator(1)).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, EntryCount(_directory.QueueDirectory));
        Assert.Equal(1, EntryCount(_directory.LeasesDirectory));
    }

    [Fact]
    public async Task HeldUnrecognisedEntry_IsExplicitError()
    {
        var path = Path.Combine(_directory.QueueDirectory, "foreign.ticket");
        using var foreign = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        var ex = await Assert.ThrowsAsync<ExecutionCoordinationException>(() => Admit(Coordinator(1)));
        Assert.Contains("foreign.ticket", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BudgetBecomingInvalidWhileQueued_FailsExplicitly_AndRemovesTicket()
    {
        SetBudget("""{ "cpuSlots": 1 }""");
        using var holder = await Admit(Coordinator(1));
        var log = new StatusLog();
        var queued = Admit(Coordinator(1), log);
        await WaitForAsync(() => log.Last is not null, "run was not queued");

        SetBudget("""{ "cpuSlots": "lots" }""");

        _ = await Assert.ThrowsAsync<ExecutionCoordinationException>(() => queued.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(0, EntryCount(_directory.QueueDirectory));
    }

    [Fact]
    public async Task RaisingTheBudget_AdmitsQueuedRuns()
    {
        SetBudget("""{ "cpuSlots": 1 }""");
        using var holder = await Admit(Coordinator(1));
        var log = new StatusLog();
        var queued = Admit(Coordinator(1), log);
        await WaitForAsync(() => log.Last is not null, "run was not queued");

        SetBudget("""{ "cpuSlots": 2 }""");

        using var lease = await queued.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, lease.Status!.BudgetCpuSlots);
    }

    [Fact]
    public async Task Initialize_RejectsReservationThatCanNeverBeAdmitted()
    {
        SetBudget("""{ "cpuSlots": 2 }""");
        var coordinator = new MachineExecutionCoordinator(_directory, new ExecutionReservation(3, null), Poll);

        _ = Assert.Throws<ExecutionCoordinationException>(() => coordinator.Initialize());
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(coordinator));
    }
}
