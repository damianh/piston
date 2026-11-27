// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

public enum TreeMenuAction
{
    Run,
    RunFailed,
    CopyName,
    CopyFullName,
    ExpandAll,
    CollapseAll,
    StopProject,
}

/// <summary>An entry of the test tree's context menu; a change of <paramref name="Group"/> starts a new section.</summary>
public sealed record TreeMenuItem(TreeMenuAction Action, string Label, int Group, bool Enabled = true, string? Hint = null);

/// <summary>What the test tree's context menu offers for the selected rows.</summary>
public static class TreeContextMenu
{
    public const string BusyHint = "Piston is busy; wait for the current run to finish";

    public static IReadOnlyList<TreeMenuItem> ItemsFor(IReadOnlyList<TestNode> nodes, bool canRun,
        IReadOnlyDictionary<string, ProjectRunStatusDto>? projectStatuses = null, string? solutionPath = null)
    {
        if (nodes.Count == 0)
        {
            return [];
        }

        var items = new List<TreeMenuItem>();
        var hint = canRun ? null : BusyHint;
        var total = TreeSelection.TestCount(nodes);
        if (total > 0)
        {
            var single = nodes is [{ Kind: TestNodeKind.Test }];
            items.Add(new(TreeMenuAction.Run, single ? "Run test" : $"Run {Tests(total)}", 0, canRun, hint));
            var failed = TreeSelection.TestCount(nodes, failedOnly: true);
            if (failed > 0 && failed < total)
            {
                items.Add(new(TreeMenuAction.RunFailed, $"Run {failed:N0} failed", 0, canRun, hint));
            }
        }

        var plural = nodes.Count > 1 ? "s" : "";
        items.Add(new(TreeMenuAction.CopyName, $"Copy name{plural}", 1));
        if (nodes.Any(n => FullName(n) != n.Label))
        {
            items.Add(new(TreeMenuAction.CopyFullName, $"Copy full name{plural}", 1));
        }

        if (nodes.Any(n => n.Children.Count > 0))
        {
            items.Add(new(TreeMenuAction.ExpandAll, "Expand all below", 2));
            items.Add(new(TreeMenuAction.CollapseAll, "Collapse all below", 2));
        }

        if (RunningProjectPath(nodes, projectStatuses, solutionPath) is not null)
        {
            items.Add(new(TreeMenuAction.StopProject, "Stop project run", 3,
                Hint: "Stop this project's run and leave the other projects running"));
        }

        return items;
    }

    internal static string? RunningProjectPath(IReadOnlyList<TestNode> nodes,
        IReadOnlyDictionary<string, ProjectRunStatusDto>? projectStatuses, string? solutionPath)
    {
        if (nodes is not [var node] || TreeSelection.ProjectOf(node) is not { } id
            || projectStatuses is null || solutionPath is null)
        {
            return null;
        }

        foreach (var (path, status) in projectStatuses)
        {
            if (status == ProjectRunStatusDto.Running
                && ProjectPaths.MatchesProjectId(path, solutionPath, id))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>The selected rows' names, or full names, one per line.</summary>
    public static string CopyText(IEnumerable<TestNode> nodes, bool fullName) =>
        string.Join('\n', nodes.Select(n => fullName ? FullName(n) : n.Label));

    /// <summary>A test's fully qualified name, a namespace's or class's full name, or a project's ID.</summary>
    public static string FullName(TestNode node) => node switch
    {
        { Test: { } test } => test.FullyQualifiedName,
        { Structure: NodeStructure.Namespace or NodeStructure.Class, GroupKey: { } group } => group,
        { Structure: NodeStructure.Project } => TreeSelection.ProjectOf(node)!,
        _ => node.Label,
    };

    /// <summary>Expands or collapses <paramref name="node"/> and everything below it, reporting each group changed.</summary>
    public static void SetExpandedBelow(TestNode node, bool expanded, Action<TestNode> changed)
    {
        if (node.Children.Count == 0)
        {
            return;
        }

        node.IsExpanded = expanded;
        changed(node);
        foreach (var child in node.Children)
        {
            SetExpandedBelow(child, expanded, changed);
        }
    }

    private static string Tests(int count) => count == 1 ? "1 test" : $"{count:N0} tests";
}
