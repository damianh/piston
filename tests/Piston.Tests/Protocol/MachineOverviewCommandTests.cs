// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Engine.Execution;
using Piston.Hosting.Protocol;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Tests.Mcp;
using Xunit;

namespace Piston.Tests.Protocol;

public sealed class MachineOverviewCommandTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-machine-command-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task MachineGetOverview_UnavailableOwnersPreservesOwnershipError()
    {
        var overview = new MachineExecutionOverview(
            DateTimeOffset.UtcNow, new ExecutionBudget(2, 1024, "/budget.json"),
            [], [], [], "Could not read worktree ownership", OwnersAvailable: false);
        var dispatcher = new EngineCommandDispatcher(new StubEngine());
        dispatcher.SetMachineOverviewProvider(_ => Task.FromResult(overview));

        var response = await dispatcher.HandleCommandAsync(ProtocolMethods.MachineGetOverview, null, _ct);
        var dto = response!.Deserialize<MachineOverviewDto>(JsonRpcSerializer.Options)!;

        Assert.False(dto.OwnersAvailable);
        Assert.True(dto.CoordinationAvailable);
        Assert.Empty(dto.Worktrees);
        Assert.Equal("Could not read worktree ownership", dto.Error);
    }

    [Fact]
    public async Task MachineGetOverview_UnavailableScanReturnsUnknownStateAndUsage()
    {
        var owner = new OwnershipRecord(
            "worktree-key", OwnershipLiveness.Live,
            new ExecutionOwnerInfo(123, "/repo/App.slnx", "/repo", DateTimeOffset.UtcNow, "daemon"));
        var overview = new MachineExecutionOverview(
            DateTimeOffset.UtcNow, new ExecutionBudget(2, 1024, "/budget.json"),
            [owner], [], [], "Scan failed", CoordinationAvailable: false);
        var dispatcher = new EngineCommandDispatcher(new StubEngine());
        dispatcher.SetMachineOverviewProvider(_ => Task.FromResult(overview));

        var response = await dispatcher.HandleCommandAsync(ProtocolMethods.MachineGetOverview, null, _ct);
        var dto = response!.Deserialize<MachineOverviewDto>(JsonRpcSerializer.Options)!;

        Assert.False(dto.CoordinationAvailable);
        Assert.Equal("Unknown", Assert.Single(dto.Worktrees).State);
        Assert.NotNull(dto.Budget);
        Assert.Null(dto.Budget.CpuSlotsInUse);
        Assert.Null(dto.Budget.MemoryMbInUse);
        Assert.Null(dto.Budget.RunningCount);
        Assert.Null(dto.Budget.QueuedCount);
        Assert.Equal("Scan failed", dto.Error);
    }

    [Fact]
    public async Task MachineGetOverview_ReturnsSafeDashboardUrlAndCurrentOwner()
    {
        var directory = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        directory.EnsureReady();
        var worktree = Path.Combine(_root, "worktree");
        _ = Directory.CreateDirectory(Path.Combine(worktree, ".git"));
        var solution = Path.Combine(worktree, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution />", _ct);
        using var owner = WorktreeOwnership.Acquire(
            directory, solution, "daemon", dashboardUrl: "http://localhost:5199");
        var dispatcher = new EngineCommandDispatcher(new StubEngine());
        dispatcher.SetMachineOverviewProvider(ct => MachineExecutionOverviewReader.ReadAsync(directory, ct));

        var response = await dispatcher.HandleCommandAsync(ProtocolMethods.MachineGetOverview, null, _ct);
        var dto = response!.Deserialize<MachineOverviewDto>(JsonRpcSerializer.Options)!;

        var worktreeDto = Assert.Single(dto.Worktrees);
        Assert.Equal(owner.Worktree.RootPath, worktreeDto.WorktreePath);
        Assert.True(worktreeDto.IsCurrent);
        Assert.Equal("Idle", worktreeDto.State);
        Assert.Equal("http://localhost:5199", worktreeDto.DashboardUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MachineGetOverview_InvalidDashboardMetadataDoesNotHideOwnersOrUsage(bool stale)
    {
        var directory = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        directory.EnsureReady();
        var healthyRoot = Path.Combine(_root, "healthy");
        var invalidRoot = Path.Combine(_root, "invalid");
        _ = Directory.CreateDirectory(Path.Combine(healthyRoot, ".git"));
        _ = Directory.CreateDirectory(Path.Combine(invalidRoot, ".git"));
        using var healthy = WorktreeOwnership.Acquire(
            directory, Path.Combine(healthyRoot, "App.slnx"), dashboardUrl: "http://localhost:5199");
        using var invalid = WorktreeOwnership.Acquire(directory, Path.Combine(invalidRoot, "App.slnx"));
        invalid.PublishDashboardUrl("http://evil.example:1234");
        if (stale)
        {
            var infoPath = Path.Combine(directory.BaseDirectory, "v1", "owners", $"{invalid.Worktree.Key}.json");
            var metadata = await File.ReadAllTextAsync(infoPath, _ct);
            invalid.Dispose();
            await File.WriteAllTextAsync(infoPath, metadata, _ct);
        }
        var dispatcher = new EngineCommandDispatcher(new StubEngine());
        dispatcher.SetMachineOverviewProvider(ct => MachineExecutionOverviewReader.ReadAsync(directory, ct));

        var response = await dispatcher.HandleCommandAsync(ProtocolMethods.MachineGetOverview, null, _ct);
        var dto = response!.Deserialize<MachineOverviewDto>(JsonRpcSerializer.Options)!;

        Assert.True(dto.OwnersAvailable);
        Assert.True(dto.CoordinationAvailable);
        Assert.Null(dto.Error);
        Assert.Equal(2, dto.Worktrees.Count);
        Assert.Equal("http://localhost:5199",
            Assert.Single(dto.Worktrees, w => w.WorktreePath == healthyRoot).DashboardUrl);
        var invalidDto = Assert.Single(dto.Worktrees, w => w.WorktreePath == invalidRoot);
        Assert.Null(invalidDto.DashboardUrl);
        Assert.Equal(stale ? "Stale" : "Live", invalidDto.Liveness);
        Assert.NotNull(dto.Budget);
        _ = Assert.NotNull(dto.Budget.RunningCount);
        _ = Assert.NotNull(dto.Budget.QueuedCount);
    }
}
