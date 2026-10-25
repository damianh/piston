// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>Text for test projects waiting in the machine-wide test resource queue.</summary>
public static class ResourceWaitText
{
    /// <summary>Waiting test projects keyed by suite name (the project file name without extension).</summary>
    public static Dictionary<string, TestResourceWaitDto> BySuiteName(IReadOnlyDictionary<string, TestResourceWaitDto>? waits) =>
        (waits ?? new Dictionary<string, TestResourceWaitDto>())
            .Where(w => w.Value.State == "Waiting")
            .GroupBy(w => Path.GetFileNameWithoutExtension(w.Key), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

    /// <summary>"port:5432 held by Api.Tests in /src/other (pid 42)".</summary>
    public static string Blocker(TestResourceBlockerDto blocker, TestResourceWaitDto wait)
    {
        var resource = blocker.Resource == "containerSlots"
            ? $"container slots ({wait.ContainerSlotsInUse}/{wait.ContainerSlotBudget} in use)"
            : blocker.Resource;
        var how = blocker.Kind == "Held" ? "held by" : "queued ahead for";
        var who = blocker.Project is { } project ? Path.GetFileNameWithoutExtension(project) : "a test project";
        var where = blocker.Worktree is { } worktree ? $" in {worktree}" : "";
        return $"{resource} {how} {who}{where} (pid {blocker.ProcessId})";
    }

    /// <summary>A multi-line tooltip: what the project waits for and who holds it.</summary>
    public static string Describe(string project, TestResourceWaitDto wait, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            $"{Path.GetFileNameWithoutExtension(project)} waiting {TestNode.FormatDuration(Math.Max(0, (now - wait.WaitingSince).TotalMilliseconds))} for exclusive test resources",
        };
        if (wait.Keys.Count > 0)
        {
            lines.Add($"Needs: {string.Join(", ", wait.Keys)}");
        }
        if (wait.UsesContainers && wait.ContainerSlotBudget is { } budget)
        {
            lines.Add($"Container slots: {wait.ContainerSlotsInUse}/{budget} in use");
        }
        lines.AddRange(wait.BlockedBy.Select(b => "Blocked: " + Blocker(b, wait)));
        return string.Join(Environment.NewLine, lines);
    }
}
