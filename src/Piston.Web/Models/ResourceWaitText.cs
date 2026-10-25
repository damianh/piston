// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>Text for test projects waiting in the machine-wide test resource queue.</summary>
public static class ResourceWaitText
{
    /// <summary>Waiting test projects keyed by suite name, combining waits for projects with the same name.</summary>
    public static Dictionary<string, TestResourceWaitDto> BySuiteName(IReadOnlyDictionary<string, TestResourceWaitDto>? waits) =>
        (waits ?? new Dictionary<string, TestResourceWaitDto>())
            .Where(w => w.Value.State == "Waiting")
            .GroupBy(w => SuiteName(w.Key), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Combine(g.Select(w => w.Value).ToList()), StringComparer.Ordinal);

    private static string SuiteName(string project) =>
        Path.GetFileNameWithoutExtension(project.Replace('\\', '/'));

    private static TestResourceWaitDto Combine(List<TestResourceWaitDto> waits) => waits[0] with
    {
        Keys = [.. waits.SelectMany(w => w.Keys).Distinct(StringComparer.Ordinal)],
        UsesContainers = waits.Any(w => w.UsesContainers),
        BlockedBy = [.. waits.SelectMany(w => w.BlockedBy).Distinct()],
        WaitingSince = waits.Min(w => w.WaitingSince),
        QueuedAhead = waits.Max(w => w.QueuedAhead),
        ContainerSlotsInUse = waits.Max(w => w.ContainerSlotsInUse),
        ContainerSlotBudget = waits.Select(w => w.ContainerSlotBudget).FirstOrDefault(b => b is not null),
    };

    /// <summary>"port:5432 held by Api.Tests in /src/other (pid 42)".</summary>
    public static string Blocker(TestResourceBlockerDto blocker, TestResourceWaitDto wait)
    {
        var resource = blocker.Resource == "containerSlots"
            ? $"container slots ({wait.ContainerSlotsInUse}/{wait.ContainerSlotBudget} in use)"
            : blocker.Resource;
        var how = blocker.Kind == "Held" ? "held by" : "queued ahead for";
        var who = blocker.Project is { } project ? SuiteName(project) : "a test project";
        var where = blocker.Worktree is { } worktree ? $" in {worktree}" : "";
        return $"{resource} {how} {who}{where} (pid {blocker.ProcessId})";
    }

    /// <summary>A multi-line tooltip: what the project waits for and who holds it.</summary>
    public static string Describe(string project, TestResourceWaitDto wait, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            $"{SuiteName(project)} waiting {TestNode.FormatDuration(Math.Max(0, (now - wait.WaitingSince).TotalMilliseconds))} for exclusive test resources",
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
