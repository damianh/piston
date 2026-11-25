// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;

namespace Piston.Hosting;

public static class StatusAllFormatter
{
    public static string Format(MachineExecutionOverview overview)
    {
        var lines = new List<string>();
        var live = overview.Owners.Where(owner => owner.Liveness == OwnershipLiveness.Live).ToList();
        var stale = overview.Owners.Where(owner => owner.Liveness == OwnershipLiveness.Stale).ToList();
        if (!overview.OwnersAvailable)
        {
            lines.Add("Worktree ownership is unknown.");
        }
        else if (live.Count == 0)
        {
            lines.Add("No Piston process owns a worktree.");
        }

        foreach (var record in live)
        {
            var owner = record.Owner;
            var dashboard = owner?.DashboardUrl is { } url ? $" | dashboard {url}" : "";
            lines.Add($"Live : {OwnerLookup.Describe(record)}{dashboard}");
        }

        foreach (var record in stale)
        {
            lines.Add($"Stale: {OwnerLookup.Describe(record)} (ownership released; diagnostic record could not be pruned)");
        }

        if (overview.Error is { Length: > 0 } error)
        {
            lines.Add($"Overview error: {error}");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
