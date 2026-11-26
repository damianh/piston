// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Protocol.Messages;

namespace Piston.Web.Models;

/// <summary>
/// The rows selected in the test tree, by <see cref="TreeRow.Key"/>. Empty means only the details pane's test or
/// node is selected (e.g. one another view revealed).
/// </summary>
public sealed class TreeSelection
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>The row a Shift+click range extends from: the last row clicked without Shift.</summary>
    public string? Anchor { get; private set; }

    public int Count => _keys.Count;

    public bool Contains(string rowKey) => _keys.Contains(rowKey);

    /// <summary>Selects only <paramref name="rowKey"/>.</summary>
    public void Select(string rowKey)
    {
        _keys.Clear();
        _ = _keys.Add(rowKey);
        Anchor = rowKey;
    }

    /// <summary>Adds or removes <paramref name="rowKey"/>; true when it is now selected.</summary>
    public bool Toggle(string rowKey)
    {
        Anchor = rowKey;
        if (_keys.Remove(rowKey))
        {
            return false;
        }

        _ = _keys.Add(rowKey);
        return true;
    }

    /// <summary>
    /// Selects the visible rows from the anchor to <paramref name="rowKey"/>, inclusive; only that row when there is
    /// no visible anchor. The anchor stays put, so further Shift+clicks re-range from it.
    /// </summary>
    public void ExtendTo(IReadOnlyList<TreeRow> rows, string rowKey)
    {
        var target = IndexOf(rows, rowKey);
        var anchor = Anchor is { } a ? IndexOf(rows, a) : -1;
        if (target < 0 || anchor < 0)
        {
            Select(rowKey);
            return;
        }

        _keys.Clear();
        for (var i = Math.Min(anchor, target); i <= Math.Max(anchor, target); i++)
        {
            _ = _keys.Add(rows[i].Key);
        }
    }

    public void Clear()
    {
        _keys.Clear();
        Anchor = null;
    }

    /// <summary>The selected nodes in tree order, including ones inside collapsed groups.</summary>
    public IReadOnlyList<TestNode> Resolve(IEnumerable<TestNode> tree) =>
        _keys.Count == 0 ? [] : [.. TreeRow.FlattenAll(tree).Where(r => _keys.Contains(r.Key)).Select(r => r.Node)];

    /// <summary>Drops rows that are no longer in <paramref name="tree"/>, e.g. after the filter changes.</summary>
    public void Prune(IEnumerable<TestNode> tree)
    {
        if (_keys.Count == 0)
        {
            return;
        }

        var present = TreeRow.FlattenAll(tree).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        _ = _keys.RemoveWhere(k => !present.Contains(k));
        if (Anchor is { } anchor && !present.Contains(anchor))
        {
            Anchor = null;
        }
    }

    /// <summary>
    /// The request that runs the tests under <paramref name="nodes"/>, or only the failed ones when
    /// <paramref name="failedOnly"/>; null when there are none. Selecting only projects runs them whole.
    /// </summary>
    public static RunTestsCommand? RunFor(IReadOnlyCollection<TestNode> nodes, bool failedOnly = false)
    {
        if (!failedOnly && nodes.Count > 0 && nodes.All(n => ProjectOf(n) is not null))
        {
            return new RunTestsCommand([.. nodes.Select(n => ProjectOf(n)!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
        }

        var tests = nodes.SelectMany(Leaves)
            .Where(l => !failedOnly || l.Test!.Status == TestStatusDto.Failed)
            .ToList();
        return tests.Count == 0 ? null : new RunTestsCommand(
            [.. tests.Select(l => l.Project!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. tests.Select(l => l.Test!.FullyQualifiedName).Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>The number of distinct tests under <paramref name="nodes"/>, optionally only the failed ones.</summary>
    public static int TestCount(IEnumerable<TestNode> nodes, bool failedOnly = false) =>
        nodes.SelectMany(Leaves)
            .Where(l => !failedOnly || l.Test!.Status == TestStatusDto.Failed)
            .Select(l => (l.Project, l.Test!.Key))
            .Distinct()
            .Count();

    /// <summary>The project a project row stands for; null for other rows.</summary>
    public static string? ProjectOf(TestNode node) =>
        node.Structure == NodeStructure.Project ? node.ExpansionKey!["suite:".Length..] : null;

    private static IEnumerable<TestNode> Leaves(TestNode node) =>
        node.Kind == TestNodeKind.Test
            ? node.Test is not null && node.Project is not null ? [node] : []
            : node.Children.SelectMany(Leaves);

    private static int IndexOf(IReadOnlyList<TreeRow> rows, string key)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }
}
