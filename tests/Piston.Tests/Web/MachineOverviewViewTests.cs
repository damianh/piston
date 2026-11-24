// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class MachineOverviewViewTests
{
    [Fact]
    public void Worktrees_OrdersLiveOwnersBeforeStale()
    {
        var items = new[]
        {
            Worktree("stale", "Stale"),
            Worktree("second", "Live"),
            Worktree("first", "Live"),
        };

        var ordered = MachineOverviewView.Worktrees(new MachineOverviewDto(DateTimeOffset.UtcNow, items, null));

        Assert.Equal(["first", "second", "stale"], ordered.Select(item => item.Key));
    }

    [Fact]
    public void DashboardLinkLabel_IdentifiesLocalAndMcpSessions()
    {
        Assert.Equal("This dashboard", MachineOverviewView.DashboardLinkLabel(Worktree("current", "Live", isCurrent: true)));
        Assert.Equal("Open dashboard", MachineOverviewView.DashboardLinkLabel(Worktree("other", "Live", url: "http://localhost:5200")));
        Assert.Equal("MCP session", MachineOverviewView.DashboardLinkLabel(Worktree("mcp", "Live", host: "mcp")));
        Assert.Equal("Unavailable", MachineOverviewView.DashboardLinkLabel(Worktree("unknown", "Live")));
        Assert.Equal("Unavailable", MachineOverviewView.DashboardLinkLabel(Worktree("stale", "Stale", url: "http://localhost:5200", isCurrent: true)));
    }

    private static MachineWorktreeDto Worktree(
        string key, string liveness, bool isCurrent = false, string? url = null, string? host = null) =>
        new(key, liveness, 123, $"/{key}/App.slnx", $"/{key}", DateTimeOffset.UtcNow,
            host, url, isCurrent);
}
