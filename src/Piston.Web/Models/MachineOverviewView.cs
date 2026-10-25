// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

public static class MachineOverviewView
{
    public static IReadOnlyList<MachineWorktreeDto> Worktrees(MachineOverviewDto? overview) =>
        overview?.Worktrees
            .OrderBy(worktree => StateOrder(worktree.State))
            .ThenBy(worktree => worktree.WorktreePath, StringComparer.Ordinal)
            .ToList() ?? [];

    public static string DashboardLinkLabel(MachineWorktreeDto worktree) =>
        worktree.IsCurrent ? "This dashboard"
        : worktree.Liveness != "Live" ? "Unavailable"
        : worktree.DashboardUrl is not null ? "Open dashboard"
        : string.Equals(worktree.Host, "mcp", StringComparison.OrdinalIgnoreCase) ? "MCP session"
        : "Unavailable";

    public static string StateColor(string state) => state switch
    {
        "Running" => "text-cyan-400",
        "Queued" => "text-yellow-400",
        "Idle" => "text-green-400",
        "Stale" => "text-gray-500",
        _ => "text-gray-400",
    };

    private static int StateOrder(string state) => state switch
    {
        "Running" => 0,
        "Queued" => 1,
        "Idle" => 2,
        "Stale" => 3,
        _ => 4,
    };
}
