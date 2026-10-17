// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>
/// Represents a node in the test tree hierarchy.
/// </summary>
public sealed class TestNode
{
    public required string Label { get; init; }
    public required TestNodeKind Kind { get; init; }
    public TestResultDto? Test { get; init; }
    public TestSuiteDto? Suite { get; init; }
    public string? GroupKey { get; init; }

    /// <summary>Stable identity used to remember this node's expansion across tree rebuilds.</summary>
    public string? ExpansionKey { get; init; }
    public List<TestNode> Children { get; } = [];
    public bool IsExpanded { get; set; } = true;
    public bool IsStale { get; init; }

    /// <summary>Target framework shown beside a test leaf in views without a framework level.</summary>
    public string? Framework { get; init; }

    /// <summary>What a non-test node groups, which <see cref="TestTreeBuilder"/> encodes in its expansion key.</summary>
    public NodeStructure Structure => StructureOf(ExpansionKey);

    public static NodeStructure StructureOf(string? expansionKey) => expansionKey switch
    {
        { } k when k.StartsWith("suite:", StringComparison.Ordinal) => NodeStructure.Project,
        { } k when k.StartsWith("tfm:", StringComparison.Ordinal) => NodeStructure.Framework,
        { } k when k.StartsWith("ns:", StringComparison.Ordinal) => NodeStructure.Namespace,
        { } k when k.StartsWith("class:", StringComparison.Ordinal) => NodeStructure.Class,
        { } k when k.StartsWith("status:", StringComparison.Ordinal) => NodeStructure.Status,
        "pinned" => NodeStructure.Pinned,
        _ => NodeStructure.None,
    };

    /// <summary>The tests under this node, or the node's own test.</summary>
    public IEnumerable<TestResultDto> Tests()
    {
        if (Test is not null)
        {
            yield return Test;
        }

        foreach (var test in Children.SelectMany(c => c.Tests()))
        {
            yield return test;
        }
    }

    /// <summary>The node with <paramref name="expansionKey"/> in <paramref name="nodes"/> or their descendants.</summary>
    public static TestNode? Find(IEnumerable<TestNode> nodes, string expansionKey)
    {
        foreach (var node in nodes)
        {
            if (node.ExpansionKey == expansionKey)
            {
                return node;
            }

            if (Find(node.Children, expansionKey) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}

public enum TestNodeKind
{
    Suite,
    Group,
    Test,
}

public enum NodeStructure
{
    None,
    Project,
    Framework,
    Namespace,
    Class,
    Status,
    Pinned,
}
