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
    public string? Project { get; init; }
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

    /// <summary>
    /// How many of the tests under this node there are, how many passed and failed, and their summed duration
    /// (excluding running tests, whose duration is not final).
    /// </summary>
    public NodeCounts Counts()
    {
        int total = 0, passed = 0, failed = 0;
        double durationMs = 0;
        foreach (var test in Tests())
        {
            total++;
            if (test.Status != TestStatusDto.Running && test.DurationMs > 0)
            {
                durationMs += test.DurationMs;
            }

            if (test.Status == TestStatusDto.Passed)
            {
                passed++;
            }
            else if (test.Status == TestStatusDto.Failed)
            {
                failed++;
            }
        }

        return new(total, passed, failed, durationMs);
    }

    /// <summary>
    /// True when <paramref name="test"/> finished, so its duration was measured. Runners report a test that took under a
    /// millisecond as 0 ms.
    /// </summary>
    public static bool HasDuration(TestResultDto test) => test.Status is TestStatusDto.Passed or TestStatusDto.Failed;

    /// <summary>Formats a duration compactly, e.g. <c>&lt;1ms</c>, <c>85ms</c>, <c>2.4s</c> or <c>3m 12s</c>.</summary>
    public static string FormatDuration(double ms) =>
        ms < 1 ? "<1ms" : ms < 1000 ? $"{(int)ms}ms" : ms < 60_000 ? $"{ms / 1000.0:F1}s" : $"{(int)(ms / 60_000)}m {(int)(ms % 60_000 / 1000)}s";

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

/// <summary>Result counts and summed duration for the tests under a tree node.</summary>
public readonly record struct NodeCounts(int Total, int Passed, int Failed, double DurationMs = 0)
{
    /// <summary>A description for screen readers and tooltips, e.g. <c>12 tests: 10 passed, 2 failed</c>.</summary>
    public string Description =>
        $"{Total:N0} {(Total == 1 ? "test" : "tests")}: {Passed:N0} passed, {Failed:N0} failed";

    /// <summary>A description of <see cref="DurationMs"/>, e.g. <c>2.4s total test time</c>.</summary>
    public string DurationDescription => $"{TestNode.FormatDuration(DurationMs)} total test time";
}

public enum TestNodeKind
{
    Suite,
    Group,
    Test,
}

public enum TestSort
{
    Name,
    Duration,
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
