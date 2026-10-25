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
            var running = owner is not null && overview.Running.Any(entry => entry.ProcessId == owner.ProcessId);
            var queued = owner is not null
                ? overview.Queue.FirstOrDefault(entry => entry.ProcessId == owner.ProcessId)
                : null;
            var state = running ? "Running"
                : queued is not null ? $"Queued #{queued.QueuePosition}"
                : owner is null || !overview.CoordinationAvailable ? "Unknown"
                : "Idle";
            var dashboard = owner?.DashboardUrl is { } url ? $" | dashboard {url}" : "";
            lines.Add($"Live : [{state}] {OwnerLookup.Describe(record)}{dashboard}");
        }

        foreach (var record in stale)
        {
            lines.Add($"Stale: {OwnerLookup.Describe(record)} (owner exited without cleanup; harmless)");
        }

        if (overview.Budget is { } budget)
        {
            var memoryInUse = overview.MemoryMbInUse?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
            var memory = budget.MemoryMb is { } maxMemory
                ? $"{memoryInUse}/{maxMemory.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} MiB"
                : $"{memoryInUse} MiB (not budgeted)";
            var cpuInUse = overview.CpuSlotsInUse?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
            var counts = overview.CoordinationAvailable
                ? $"{overview.Running.Count} running, {overview.Queue.Count} queued"
                : "running and queued counts unknown";
            lines.Add(
                $"Budget: CPU {cpuInUse}/{budget.CpuSlots} slot(s); memory {memory}; " +
                $"{counts} ({budget.Source})");
        }

        foreach (var entry in overview.Running)
        {
            lines.Add(
                $"Running: PID {entry.ProcessId} | run {entry.RunId} | {entry.CpuSlots} CPU slot(s), " +
                $"memory {entry.MemoryMb?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} MiB" +
                (entry.WorktreeKey is { } key ? $" | worktree {key}" : " | unknown worktree"));
        }

        foreach (var entry in overview.Queue)
        {
            lines.Add(
                $"Queue #{entry.QueuePosition}: PID {entry.ProcessId} | run {entry.RunId} | " +
                $"{entry.CpuSlots} CPU slot(s), " +
                $"memory {entry.MemoryMb?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} MiB" +
                (entry.WorktreeKey is { } key ? $" | worktree {key}" : " | unknown worktree"));
        }

        if (overview.Error is { Length: > 0 } error)
        {
            lines.Add($"Overview error: {error}");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
