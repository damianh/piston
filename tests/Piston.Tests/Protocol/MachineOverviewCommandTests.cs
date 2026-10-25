// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using System.Text.Json;
using Piston.Hosting.Protocol;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
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
}
