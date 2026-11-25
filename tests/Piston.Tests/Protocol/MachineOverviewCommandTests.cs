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
            DateTimeOffset.UtcNow, [], "Could not read worktree ownership", OwnersAvailable: false);
        var dispatcher = new EngineCommandDispatcher(new StubEngine());
        dispatcher.SetMachineOverviewProvider(_ => Task.FromResult(overview));

        var response = await dispatcher.HandleCommandAsync(ProtocolMethods.MachineGetOverview, null, _ct);
        var dto = response!.Deserialize<MachineOverviewDto>(JsonRpcSerializer.Options)!;

        Assert.False(dto.OwnersAvailable);
        Assert.Empty(dto.Worktrees);
        Assert.Equal("Could not read worktree ownership", dto.Error);
    }

    [Fact]
    public async Task MachineGetOverview_ReturnsOwnershipWithoutRunStateOrBudgets()
    {
        var owner = new OwnershipRecord(
            "worktree-key", OwnershipLiveness.Live,
            new ExecutionOwnerInfo(123, "/repo/App.slnx", "/repo", DateTimeOffset.UtcNow, "daemon"));
        var overview = new MachineExecutionOverview(
            DateTimeOffset.UtcNow, [owner], null);
        var dispatcher = new EngineCommandDispatcher(new StubEngine());
        dispatcher.SetMachineOverviewProvider(_ => Task.FromResult(overview));

        var response = await dispatcher.HandleCommandAsync(ProtocolMethods.MachineGetOverview, null, _ct);
        var dto = response!.Deserialize<MachineOverviewDto>(JsonRpcSerializer.Options)!;

        Assert.Equal("Live", Assert.Single(dto.Worktrees).Liveness);
        var json = response!.ToJsonString();
        Assert.DoesNotContain("\"budget\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"running\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"queue\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"state\"", json, StringComparison.Ordinal);
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
        Assert.Equal("Live", worktreeDto.Liveness);
        Assert.Equal("http://localhost:5199", worktreeDto.DashboardUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MachineGetOverview_InvalidDashboardMetadataDoesNotHideOwners(bool stale)
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
        invalid.PublishDashboardUrl("http://localhost:1234");
        var infoPath = Path.Combine(directory.BaseDirectory, "v1", "owners", $"{invalid.Worktree.Key}.json");
        var metadata = (await File.ReadAllTextAsync(infoPath, _ct))
            .Replace("http://localhost:1234", "http://evil.example:1234", StringComparison.Ordinal);
        await File.WriteAllTextAsync(infoPath, metadata, _ct);
        if (stale)
        {
            invalid.Dispose();
            await File.WriteAllTextAsync(infoPath, metadata, _ct);
        }
        var dispatcher = new EngineCommandDispatcher(new StubEngine());
        dispatcher.SetMachineOverviewProvider(ct => MachineExecutionOverviewReader.ReadAsync(directory, ct));

        var response = await dispatcher.HandleCommandAsync(ProtocolMethods.MachineGetOverview, null, _ct);
        var dto = response!.Deserialize<MachineOverviewDto>(JsonRpcSerializer.Options)!;

        Assert.True(dto.OwnersAvailable);
        Assert.Null(dto.Error);
        Assert.Equal(stale ? 1 : 2, dto.Worktrees.Count);
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Assert.Equal("http://localhost:5199",
            Assert.Single(dto.Worktrees, w => pathComparer.Equals(w.WorktreePath, healthyRoot)).DashboardUrl);
        if (stale)
        {
            Assert.False(File.Exists(infoPath));
        }
        else
        {
            var invalidDto = Assert.Single(dto.Worktrees, w => pathComparer.Equals(w.WorktreePath, invalidRoot));
            Assert.Null(invalidDto.DashboardUrl);
            Assert.Equal("Live", invalidDto.Liveness);
        }
    }
}
