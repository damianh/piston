// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public class TestNodeTests
{
    [Fact]
    public void Counts_CoverEveryDescendantTest()
    {
        var inner = Group("class:B", Leaf("B.One", TestStatusDto.Failed, 900), Leaf("B.Two", TestStatusDto.Skipped, 0));
        var root = Group("ns:A", Leaf("A.One", TestStatusDto.Passed, 250), Leaf("A.Two", TestStatusDto.Passed, 100), inner);

        var counts = root.Counts();

        Assert.Equal(new NodeCounts(4, 2, 1, 1250), counts);
        Assert.Equal("4 tests: 2 passed, 1 failed", counts.Description);
        Assert.Equal("1.2s total test time", counts.DurationDescription);
        Assert.Equal(new NodeCounts(2, 0, 1, 900), inner.Counts());
    }

    [Fact]
    public void Counts_ExcludeRunningTestsFromDuration()
    {
        var root = Group("class:A", Leaf("A.One", TestStatusDto.Passed, 40), Leaf("A.Two", TestStatusDto.Running, 500));

        Assert.Equal(new NodeCounts(2, 1, 0, 40), root.Counts());
    }

    [Fact]
    public void Description_SingularTest() =>
        Assert.Equal("1 test: 1 passed, 0 failed", new NodeCounts(1, 1, 0).Description);

    [Theory]
    [InlineData(0, "<1ms")]
    [InlineData(0.4, "<1ms")]
    [InlineData(999.9, "999ms")]
    [InlineData(1000, "1.0s")]
    [InlineData(2400, "2.4s")]
    [InlineData(192_400, "3m 12s")]
    public void FormatDuration_ScalesUnits(double ms, string expected) =>
        Assert.Equal(expected, TestNode.FormatDuration(ms));

    private static TestNode Group(string key, params TestNode[] children)
    {
        var node = new TestNode { Label = key, Kind = TestNodeKind.Group, ExpansionKey = key };
        node.Children.AddRange(children);
        return node;
    }

    private static TestNode Leaf(string fqn, TestStatusDto status, double durationMs) => new()
    {
        Label = fqn,
        Kind = TestNodeKind.Test,
        Test = new TestResultDto(fqn, fqn, status, durationMs, null, null, null, null),
    };
}
