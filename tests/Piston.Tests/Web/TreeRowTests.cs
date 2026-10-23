// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class TreeRowTests
{
    private static TestNode Test(string name, string? project = null) => new()
    {
        Label = name,
        Kind = TestNodeKind.Test,
        Project = project,
        Test = new TestResultDto(name, name, TestStatusDto.Passed, 1, null, null, null, null),
    };

    private static TestNode Group(string name, bool expanded, params TestNode[] children)
    {
        var node = new TestNode { Label = name, Kind = TestNodeKind.Group, ExpansionKey = name, IsExpanded = expanded };
        node.Children.AddRange(children);
        return node;
    }

    private static List<TestNode> Tree(bool innerExpanded) =>
    [
        Group("P", true,
            Group("N", innerExpanded, Test("N.A"), Test("N.B")),
            Test("P.C")),
        Group("Q", false, Test("Q.D")),
    ];

    [Fact]
    public void Flatten_ListsExpandedNodesInOrderWithDepth()
    {
        var rows = TreeRow.Flatten(Tree(innerExpanded: true));

        Assert.Equal(["P:0", "N:1", "N.A:2", "N.B:2", "P.C:1", "Q:0"], rows.Select(r => $"{r.Node.Label}:{r.Depth}"));
    }

    [Fact]
    public void Flatten_SkipsChildrenOfCollapsedNodes()
    {
        var rows = TreeRow.Flatten(Tree(innerExpanded: false));

        Assert.Equal(["P", "N", "P.C", "Q"], rows.Select(r => r.Node.Label));
    }

    [Fact]
    public void IndexOfSelected_DistinguishesDuplicateTestKeysByProject()
    {
        var first = Test("Shared.Test", "A.Tests");
        var second = Test("Shared.Test", "B.Tests");
        var rows = TreeRow.Flatten([first, second]);

        Assert.Equal(1, TreeRow.IndexOfSelected(rows, "Shared.Test", null, "B.Tests"));
        Assert.Equal(-1, TreeRow.IndexOfSelected(rows, "Shared.Test", null, "Missing.Tests"));
    }

    [Fact]
    public void IndexOfSelected_FindsTheTestOrElseTheGroup()
    {
        var rows = TreeRow.Flatten(Tree(innerExpanded: true));

        Assert.Equal(3, TreeRow.IndexOfSelected(rows, "N.B", null));
        Assert.Equal(1, TreeRow.IndexOfSelected(rows, null, "N"));
        Assert.Equal(-1, TreeRow.IndexOfSelected(rows, "Q.D", null));
        Assert.Equal(-1, TreeRow.IndexOfSelected(rows, null, null));
    }
}
