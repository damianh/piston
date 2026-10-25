// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Xunit;

namespace Piston.Engine.Tests.Execution;

public sealed class MachineExecutionOverviewTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-overview-{Guid.NewGuid():N}");
    private readonly ExecutionCoordinationDirectory _directory;

    public MachineExecutionOverviewTests()
    {
        _ = Directory.CreateDirectory(_root);
        _directory = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        _directory.EnsureReady();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ReadAsync_ListsOwnedWorktreesRunningReservationsAndFifoQueue()
    {
        await File.WriteAllTextAsync(_directory.BudgetPath, """{ "cpuSlots": 1, "memoryMb": 1024 }""", _ct);
        var firstSolution = CreateSolution("first");
        var queuedSolution = CreateSolution("queued");

        using var running = LeaseHolderProcess.Start(
            "own-admit", _directory.BaseDirectory, firstSolution, "1", "512", "daemon", "http://localhost:5199");
        Assert.Equal("READY", running.WaitForLine("READY", "ERROR"));
        using var queued = LeaseHolderProcess.Start(
            "own-admit", _directory.BaseDirectory, queuedSolution, "1", "512", "mcp", "-");
        Assert.StartsWith("QUEUED", queued.WaitForLine("QUEUED", "READY", "ERROR"), StringComparison.Ordinal);

        var overview = await MachineExecutionOverviewReader.ReadAsync(_directory, _ct);

        Assert.Null(overview.Error);
        Assert.Equal(1, overview.Budget!.CpuSlots);
        Assert.Equal(1024, overview.Budget.MemoryMb);
        Assert.Equal(1, overview.CpuSlotsInUse);
        Assert.Equal(512L, overview.MemoryMbInUse);
        var records = overview.Owners.Where(owner => owner.Liveness == OwnershipLiveness.Live).ToList();
        Assert.Equal(2, records.Count);
        _ = Assert.Single(overview.Running);
        Assert.Equal(running.ProcessId, overview.Running[0].ProcessId);
        _ = Assert.Single(overview.Queue);
        Assert.Equal(queued.ProcessId, overview.Queue[0].ProcessId);
        Assert.Equal(1, overview.Queue[0].QueuePosition);
        Assert.Equal(queued.ProcessId, Assert.Single(records, record => record.Owner!.Host == "mcp").Owner!.ProcessId);
        Assert.Equal("http://localhost:5199",
            Assert.Single(records, record => record.Owner!.Host == "daemon").Owner!.DashboardUrl);
    }

    [Fact]
    public async Task ReadAsync_LockTimeoutMarksUsageUnknown()
    {
        using var admissionLock = await ExclusiveFileLock.AcquireAsync(_directory.AdmissionLockPath, _ct);

        var overview = await MachineExecutionOverviewReader.ReadAsync(_directory, _ct);

        Assert.False(overview.CoordinationAvailable);
        Assert.Null(overview.CpuSlotsInUse);
        Assert.Null(overview.MemoryMbInUse);
        Assert.Contains("Timed out", overview.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("foreign.lock", true)]
    [InlineData("invalid.lease", false)]
    public async Task ReadAsync_HeldInvalidEntryDiscardsPartialSnapshot(string name, bool inQueue)
    {
        var ticketName = $"00000000000000000001_1_0_{Environment.ProcessId}_run.ticket";
        using var ticket = ExclusiveFileLock.TryAcquire(
            Path.Combine(_directory.QueueDirectory, ticketName), FileMode.CreateNew);
        using var invalid = ExclusiveFileLock.TryAcquire(
            Path.Combine(inQueue ? _directory.QueueDirectory : _directory.LeasesDirectory, name), FileMode.CreateNew);
        Assert.NotNull(ticket);
        Assert.NotNull(invalid);

        var overview = await MachineExecutionOverviewReader.ReadAsync(_directory, _ct);

        Assert.False(overview.CoordinationAvailable);
        Assert.Empty(overview.Queue);
        Assert.Empty(overview.Running);
        Assert.Null(overview.CpuSlotsInUse);
        Assert.Contains("Unrecognised held entry", overview.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_IgnoresUnheldForeignFilesWithoutDeletingThem()
    {
        var path = Path.Combine(_directory.QueueDirectory, "foreign.lock");
        await File.WriteAllTextAsync(path, "", _ct);

        var overview = await MachineExecutionOverviewReader.ReadAsync(_directory, _ct);

        Assert.True(overview.CoordinationAvailable);
        Assert.Null(overview.Error);
        Assert.Empty(overview.Queue);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ReadAsync_DoesNotCreateCoordinationDirectory()
    {
        var missing = new ExecutionCoordinationDirectory(Path.Combine(_root, "missing"));

        var overview = await MachineExecutionOverviewReader.ReadAsync(missing, _ct);

        Assert.Empty(overview.Owners);
        Assert.Empty(overview.Running);
        Assert.Empty(overview.Queue);
        Assert.Null(overview.Error);
        Assert.False(Directory.Exists(missing.BaseDirectory));
    }

    [Fact]
    public async Task ReadAsync_ReportsInvalidBudgetWhenThereAreNoRunEntries()
    {
        await File.WriteAllTextAsync(_directory.BudgetPath, """{ "cpuSlots": 0 }""", _ct);

        var overview = await MachineExecutionOverviewReader.ReadAsync(_directory, _ct);

        Assert.Null(overview.Budget);
        Assert.Contains("Invalid machine execution budget", overview.Error, StringComparison.Ordinal);
    }

    private string CreateSolution(string name)
    {
        var worktree = Path.Combine(_root, name);
        _ = Directory.CreateDirectory(Path.Combine(worktree, ".git"));
        var solution = Path.Combine(worktree, "App.slnx");
        File.WriteAllText(solution, "<Solution />");
        return solution;
    }
}
