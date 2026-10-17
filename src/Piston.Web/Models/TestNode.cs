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
}

public enum TestNodeKind
{
    Suite,
    Group,
    Test,
}
