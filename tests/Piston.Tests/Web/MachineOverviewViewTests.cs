// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class MachineOverviewViewTests
{
    [Fact]
    public void Worktrees_OrdersActiveOwnersBeforeIdleAndStale()
    {
        var items = new[]
        {
            Worktree("idle", "Idle"),
            Worktree("stale", "Stale"),
            Worktree("queued", "Queued"),
            Worktree("running", "Running"),
        };

        var ordered = MachineOverviewView.Worktrees(new MachineOverviewDto(DateTimeOffset.UtcNow, null, items, [], [], null));

        Assert.Equal(["running", "queued", "idle", "stale"], ordered.Select(item => item.Key));
    }

    [Fact]
    public void DashboardLinkLabel_IdentifiesLocalAndMcpSessions()
    {
        Assert.Equal("This dashboard", MachineOverviewView.DashboardLinkLabel(Worktree("current", "Running", isCurrent: true)));
        Assert.Equal("Open dashboard", MachineOverviewView.DashboardLinkLabel(Worktree("other", "Idle", url: "http://localhost:5200")));
        Assert.Equal("MCP session", MachineOverviewView.DashboardLinkLabel(Worktree("mcp", "Idle", host: "mcp")));
        Assert.Equal("Unavailable", MachineOverviewView.DashboardLinkLabel(Worktree("unknown", "Idle")));
        Assert.Equal("Unavailable", MachineOverviewView.DashboardLinkLabel(Worktree("stale", "Stale", url: "http://localhost:5200", liveness: "Stale")));
    }

    private static MachineWorktreeDto Worktree(
        string key, string state, bool isCurrent = false, string? url = null, string? host = null, string liveness = "Live") =>
        new(key, liveness, 123, $"/{key}/App.slnx", $"/{key}", DateTimeOffset.UtcNow,
            host, url, isCurrent, state, state == "Queued" ? 1 : null);
}
