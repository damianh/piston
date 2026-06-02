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
