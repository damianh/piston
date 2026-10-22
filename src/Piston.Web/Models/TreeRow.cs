// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>A visible row of the test tree: a node and its depth. The tree renders a flat, virtualized list of these.</summary>
public sealed record TreeRow(TestNode Node, int Depth)
{
    /// <summary>The rendered height of a row, in pixels; rows have a fixed height so the list can be virtualized.</summary>
    public const int Height = 20;

    /// <summary>The rows shown for <paramref name="nodes"/>: each node, then the rows of its children when it is expanded.</summary>
    public static List<TreeRow> Flatten(IEnumerable<TestNode> nodes)
    {
        var rows = new List<TreeRow>();
        var stack = new Stack<(TestNode Node, int Depth)>();
        foreach (var node in nodes.Reverse())
        {
            stack.Push((node, 0));
        }

        while (stack.TryPop(out var item))
        {
            rows.Add(new TreeRow(item.Node, item.Depth));
            if (item.Node.IsExpanded)
            {
                for (var i = item.Node.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push((item.Node.Children[i], item.Depth + 1));
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// The index of the selected row: the test with <paramref name="selectedKey"/>, or else the group with
    /// <paramref name="selectedNodeKey"/>; -1 when neither is visible.
    /// </summary>
    public static int IndexOfSelected(IReadOnlyList<TreeRow> rows, string? selectedKey, string? selectedNodeKey)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var node = rows[i].Node;
            if (node.Kind == TestNodeKind.Test
                ? selectedKey is not null && node.Test?.Key == selectedKey
                : selectedNodeKey is not null && node.ExpansionKey == selectedNodeKey)
            {
                return i;
            }
        }

        return -1;
    }
}
