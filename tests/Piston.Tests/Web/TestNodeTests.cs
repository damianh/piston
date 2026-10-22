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
        var inner = Group("class:B", Leaf("B.One", TestStatusDto.Failed), Leaf("B.Two", TestStatusDto.Skipped));
        var root = Group("ns:A", Leaf("A.One", TestStatusDto.Passed), Leaf("A.Two", TestStatusDto.Passed), inner);

        var counts = root.Counts();

        Assert.Equal(new NodeCounts(4, 2, 1), counts);
        Assert.Equal("4 tests: 2 passed, 1 failed", counts.Description);
        Assert.Equal(new NodeCounts(2, 0, 1), inner.Counts());
    }

    [Fact]
    public void Description_SingularTest() =>
        Assert.Equal("1 test: 1 passed, 0 failed", new NodeCounts(1, 1, 0).Description);

    private static TestNode Group(string key, params TestNode[] children)
    {
        var node = new TestNode { Label = key, Kind = TestNodeKind.Group, ExpansionKey = key };
        node.Children.AddRange(children);
        return node;
    }

    private static TestNode Leaf(string fqn, TestStatusDto status) => new()
    {
        Label = fqn,
        Kind = TestNodeKind.Test,
        Test = new TestResultDto(fqn, fqn, status, 1, null, null, null, null),
    };
}
