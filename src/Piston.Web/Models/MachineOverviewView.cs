// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

public static class MachineOverviewView
{
    public static IReadOnlyList<MachineWorktreeDto> Worktrees(MachineOverviewDto? overview) =>
        overview?.Worktrees
            .OrderBy(worktree => worktree.Liveness == "Live" ? 0 : 1)
            .ThenBy(worktree => worktree.WorktreePath, StringComparer.Ordinal)
            .ToList() ?? [];

    public static string DashboardLinkLabel(MachineWorktreeDto worktree) =>
        worktree.Liveness != "Live" ? "Unavailable"
        : worktree.IsCurrent ? "This dashboard"
        : worktree.DashboardUrl is not null ? "Open dashboard"
        : string.Equals(worktree.Host, "mcp", StringComparison.OrdinalIgnoreCase) ? "MCP session"
        : "Unavailable";

    public static string LivenessColor(string liveness) => liveness switch
    {
        "Live" => "text-green-400",
        "Stale" => "text-gray-500",
        _ => "text-gray-400",
    };
}
