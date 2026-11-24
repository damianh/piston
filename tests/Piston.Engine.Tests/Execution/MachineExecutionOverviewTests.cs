// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Xunit;

namespace Piston.Engine.Tests.Execution;

public sealed class MachineExecutionOverviewTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("piston-overview-").FullName;
    private readonly ExecutionCoordinationDirectory _directory;

    public MachineExecutionOverviewTests()
    {
        _directory = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        _directory.EnsureReady();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ReadAsync_ListsLiveWorktreeOwnersAcrossProcesses()
    {
        using var first = LeaseHolderProcess.Start(
            "own", _directory.BaseDirectory, CreateSolution("first"), "daemon", "http://localhost:5199");
        Assert.Equal("READY", first.WaitForLine("READY", "ERROR"));
        using var second = LeaseHolderProcess.Start(
            "own", _directory.BaseDirectory, CreateSolution("second"), "mcp", "-");
        Assert.Equal("READY", second.WaitForLine("READY", "ERROR"));

        var overview = await MachineExecutionOverviewReader.ReadAsync(_directory, _ct);

        Assert.Null(overview.Error);
        Assert.True(overview.OwnersAvailable);
        var owners = overview.Owners.Where(owner => owner.Liveness == OwnershipLiveness.Live).ToList();
        Assert.Equal(2, owners.Count);
        Assert.Equal(first.ProcessId, Assert.Single(owners, owner => owner.Owner!.Host == "daemon").Owner!.ProcessId);
        Assert.Equal("http://localhost:5199",
            Assert.Single(owners, owner => owner.Owner!.Host == "daemon").Owner!.DashboardUrl);
        Assert.Equal(second.ProcessId, Assert.Single(owners, owner => owner.Owner!.Host == "mcp").Owner!.ProcessId);
    }

    [Fact]
    public async Task ReadAsync_ReportsStaleOwners()
    {
        var owner = WorktreeOwnership.Acquire(_directory, CreateSolution("stale"));
        var infoPath = Path.Combine(_directory.OwnersDirectory, owner.Worktree.Key + ".json");
        var metadata = await File.ReadAllTextAsync(infoPath, _ct);
        owner.Dispose();
        await File.WriteAllTextAsync(infoPath, metadata, _ct);

        var overview = await MachineExecutionOverviewReader.ReadAsync(_directory, _ct);

        Assert.Equal(OwnershipLiveness.Stale, Assert.Single(overview.Owners).Liveness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_InaccessibleOwnershipReportsError(bool blockLock)
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        using var owner = WorktreeOwnership.Acquire(_directory, CreateSolution("owned"));
        var path = blockLock
            ? Path.Combine(_directory.OwnersDirectory, owner.Worktree.Key + ".lock")
            : _directory.OwnersDirectory;
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var overview = await MachineExecutionOverviewReader.ReadAsync(_directory, _ct);
            Assert.False(overview.OwnersAvailable);
            Assert.Empty(overview.Owners);
            Assert.Contains("Could not read worktree ownership", overview.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    [Fact]
    public async Task ReadAsync_NonDirectoryOwnershipPathIsAnError()
    {
        var savedPath = _directory.OwnersDirectory + ".saved";
        Directory.Move(_directory.OwnersDirectory, savedPath);
        await File.WriteAllTextAsync(_directory.OwnersDirectory, "", _ct);
        try
        {
            var overview = await MachineExecutionOverviewReader.ReadAsync(_directory, _ct);
            Assert.False(overview.OwnersAvailable);
            Assert.Contains("Could not read worktree ownership", overview.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(_directory.OwnersDirectory);
            Directory.Move(savedPath, _directory.OwnersDirectory);
        }
    }

    [Fact]
    public async Task ReadAsync_IgnoresLegacyBudgetAndAdmissionFiles()
    {
        using var owner = WorktreeOwnership.Acquire(_directory, CreateSolution("owned"));
        var queue = Path.Combine(_directory.StateDirectory, "queue");
        _ = Directory.CreateDirectory(queue);
        var ticket = Path.Combine(queue, "old.ticket");
        await File.WriteAllTextAsync(ticket, "old", _ct);
        await File.WriteAllTextAsync(_directory.LegacyBudgetPath, "{ invalid", _ct);
        using var held = ExclusiveFileLock.TryAcquire(ticket, FileMode.Open);
        Assert.NotNull(held);

        var overview = await MachineExecutionOverviewReader.ReadAsync(_directory, _ct);

        Assert.Null(overview.Error);
        Assert.Equal(OwnershipLiveness.Live, Assert.Single(overview.Owners).Liveness);
        Assert.True(File.Exists(ticket));
        Assert.True(File.Exists(_directory.LegacyBudgetPath));
    }

    [Fact]
    public async Task ReadAsync_DoesNotCreateMissingCoordinationDirectory()
    {
        var missing = new ExecutionCoordinationDirectory(Path.Combine(_root, "missing"));
        var overview = await MachineExecutionOverviewReader.ReadAsync(missing, _ct);

        Assert.Empty(overview.Owners);
        Assert.Null(overview.Error);
        Assert.False(Directory.Exists(missing.BaseDirectory));
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
