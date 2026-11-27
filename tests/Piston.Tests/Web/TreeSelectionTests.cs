// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class TreeSelectionTests
{
    private static TestNode Test(string fqn, string project, TestStatusDto status = TestStatusDto.Passed) => new()
    {
        Label = fqn[(fqn.LastIndexOf('.') + 1)..],
        Kind = TestNodeKind.Test,
        Project = project,
        Test = new TestResultDto(fqn, fqn, status, 1, null, null, null, null),
    };

    private static TestNode Group(string key, string label, bool expanded, params TestNode[] children)
    {
        var node = new TestNode
        {
            Label = label,
            Kind = TestNodeKind.Group,
            ExpansionKey = key,
            GroupKey = key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : null,
            IsExpanded = expanded,
        };
        node.Children.AddRange(children);
        return node;
    }

    private static TestNode Project(string id, bool expanded, params TestNode[] children)
    {
        var node = new TestNode { Label = id, Kind = TestNodeKind.Suite, ExpansionKey = $"suite:{id}", IsExpanded = expanded };
        node.Children.AddRange(children);
        return node;
    }

    // A.Tests ▸ ns:N (collapsed) ▸ N.One (failed), N.Two; B.Tests ▸ N.Three
    private static List<TestNode> Tree() =>
    [
        Project("A.Tests", true,
            Group("ns:N", "N", false, Test("N.One", "A.Tests", TestStatusDto.Failed), Test("N.Two", "A.Tests"))),
        Project("B.Tests", true, Test("N.Three", "B.Tests")),
    ];

    [Fact]
    public void Toggle_AddsThenRemovesARow()
    {
        var selection = new TreeSelection();

        Assert.True(selection.Toggle("a"));
        Assert.True(selection.Toggle("b"));
        Assert.False(selection.Toggle("a"));

        Assert.False(selection.Contains("a"));
        Assert.True(selection.Contains("b"));
    }

    [Fact]
    public void ExtendTo_SelectsTheVisibleRangeFromAFixedAnchor()
    {
        var rows = TreeRow.Flatten(Tree());
        var selection = new TreeSelection();
        selection.Select(rows[3].Key);

        selection.ExtendTo(rows, rows[1].Key);
        Assert.Equal([rows[1].Node, rows[2].Node, rows[3].Node], selection.Resolve(Tree()), NodeByLabel);

        selection.ExtendTo(rows, rows[0].Key);
        Assert.Equal(4, selection.Count);
    }

    [Fact]
    public void ExtendTo_WithoutAnAnchorSelectsOnlyTheRow()
    {
        var rows = TreeRow.Flatten(Tree());
        var selection = new TreeSelection();

        selection.ExtendTo(rows, rows[2].Key);

        Assert.Equal(1, selection.Count);
        Assert.Equal(rows[2].Key, selection.Anchor);
    }

    [Fact]
    public void FlattenAll_KeysCollapsedRowsAsFlattenWouldWhenExpanded()
    {
        var collapsed = TreeRow.FlattenAll(Tree());
        var expanded = Tree();
        expanded[0].Children[0].IsExpanded = true;

        Assert.Equal(TreeRow.Flatten(expanded).Select(r => r.Key), collapsed.Select(r => r.Key));
    }

    [Fact]
    public void Resolve_FindsRowsInsideCollapsedGroupsAndPruneDropsRemovedOnes()
    {
        var all = TreeRow.FlattenAll(Tree());
        var hidden = all.Single(r => r.Node.Label == "One").Key;
        var selection = new TreeSelection();
        selection.Select(hidden);
        _ = selection.Toggle(all[0].Key);

        Assert.Equal(["A.Tests", "One"], selection.Resolve(Tree()).Select(n => n.Label));

        selection.Prune([Project("B.Tests", true, Test("N.Three", "B.Tests"))]);

        Assert.Equal(0, selection.Count);
        Assert.Null(selection.Anchor);
    }

    [Fact]
    public void RunFor_RunsSelectedProjectsWhole()
    {
        var tree = Tree();

        var command = TreeSelection.RunFor([tree[1], tree[0]]);

        Assert.Equal(["A.Tests", "B.Tests"], command!.ProjectIds);
        Assert.Null(command.Tests);
    }

    [Fact]
    public void RunFor_UnionsTheTestsOfAMixedSelection()
    {
        var tree = Tree();
        var ns = tree[0].Children[0];

        var command = TreeSelection.RunFor([ns, ns.Children[0], tree[1]]);

        Assert.Equal(["A.Tests", "B.Tests"], command!.ProjectIds);
        Assert.Equal(["N.One", "N.Two", "N.Three"], command.Tests);
    }

    [Fact]
    public void RunFor_FailedOnlyRunsJustTheFailedTestsOrNothing()
    {
        var tree = Tree();

        var command = TreeSelection.RunFor(tree, failedOnly: true);

        Assert.Equal(["A.Tests"], command!.ProjectIds);
        Assert.Equal(["N.One"], command.Tests);
        Assert.Null(TreeSelection.RunFor([tree[1]], failedOnly: true));
    }

    [Fact]
    public void TestCount_CountsATestOnceWhenItsGroupIsAlsoSelected()
    {
        var tree = Tree();

        Assert.Equal(3, TreeSelection.TestCount([.. tree, tree[0].Children[0].Children[1]]));
        Assert.Equal(1, TreeSelection.TestCount(tree, failedOnly: true));
    }

    [Fact]
    public void ItemsFor_OffersRunFailedOnlyForAMixOfResultsAndNoFullNamesForProjects()
    {
        var tree = Tree();

        var items = TreeContextMenu.ItemsFor(tree, canRun: true);

        Assert.Equal(["Run 3 tests", "Run 1 failed", "Copy names", "Expand all below", "Collapse all below"],
            items.Select(i => i.Label));
        Assert.DoesNotContain(TreeContextMenu.ItemsFor([tree[1]], canRun: true), i => i.Action == TreeMenuAction.RunFailed);
    }

    [Fact]
    public void ItemsFor_OffersCopyFullNamesForSeveralTests()
    {
        var tree = Tree();
        var tests = new[] { tree[0].Children[0].Children[0], tree[1].Children[0] };

        var items = TreeContextMenu.ItemsFor(tests, canRun: true);

        Assert.Equal(["Run 2 tests", "Run 1 failed", "Copy names", "Copy full names"], items.Select(i => i.Label));
        Assert.Equal("One\nThree", TreeContextMenu.CopyText(tests, fullName: false));
        Assert.Equal("N.One\nN.Three", TreeContextMenu.CopyText(tests, fullName: true));
    }

    [Fact]
    public void ItemsFor_ASingleTestHasNoExpandItems()
    {
        var test = Tree()[1].Children[0];

        var items = TreeContextMenu.ItemsFor([test], canRun: true);

        Assert.Equal(["Run test", "Copy name", "Copy full name"], items.Select(i => i.Label));
        Assert.Equal("N.Three", TreeContextMenu.CopyText([test], fullName: true));
    }

    [Fact]
    public void ItemsFor_DisablesRunningWhilePistonIsBusy()
    {
        var items = TreeContextMenu.ItemsFor(Tree(), canRun: false);

        Assert.All(items.Where(i => i.Action is TreeMenuAction.Run or TreeMenuAction.RunFailed), i =>
        {
            Assert.False(i.Enabled);
            Assert.Equal(TreeContextMenu.BusyHint, i.Hint);
        });
        Assert.True(items.Single(i => i.Action == TreeMenuAction.CopyName).Enabled);
    }

    [Fact]
    public void StopProject_UsesTheSelectedRunningProjectsPath_EvenWhenRunIsDisabled()
    {
        const string solution = @"C:\repo\App.slnx";
        const string first = @"C:\repo\One\Tests.csproj";
        const string second = @"C:\repo\Two\Tests.csproj";
        var statuses = new Dictionary<string, ProjectRunStatusDto>
        {
            [first] = ProjectRunStatusDto.Running,
            [second] = ProjectRunStatusDto.Running,
        };
        var selected = Project("two/Tests.csproj", true);

        Assert.Equal(second, TreeContextMenu.RunningProjectPath([selected], statuses, solution));
        var items = TreeContextMenu.ItemsFor([selected], false, statuses, solution);
        Assert.True(Assert.Single(items, i => i.Action == TreeMenuAction.StopProject).Enabled);

        statuses[second] = ProjectRunStatusDto.Completed;
        Assert.Null(TreeContextMenu.RunningProjectPath([selected], statuses, solution));
        Assert.DoesNotContain(TreeContextMenu.ItemsFor([selected], false, statuses, solution), i => i.Action == TreeMenuAction.StopProject);
    }

    [Fact]
    public void StopProject_IsNotOfferedForTestRowsOrMultipleProjects()
    {
        const string solution = "/repo/App.slnx";
        var statuses = new Dictionary<string, ProjectRunStatusDto>
        {
            ["/repo/A.csproj"] = ProjectRunStatusDto.Running,
            ["/repo/B.csproj"] = ProjectRunStatusDto.Running,
        };
        var test = Test("N.One", "A.csproj", TestStatusDto.Running);

        Assert.Null(TreeContextMenu.RunningProjectPath([test], statuses, solution));
        Assert.Null(TreeContextMenu.RunningProjectPath([Project("A.csproj", true), Project("B.csproj", true)], statuses, solution));
    }

    [Fact]
    public void SetExpandedBelow_ExpandsEveryGroupUnderTheNode()
    {
        var tree = Tree();
        var changed = new List<string>();

        TreeContextMenu.SetExpandedBelow(tree[0], expanded: true, n => changed.Add(n.Label));

        Assert.True(tree[0].Children[0].IsExpanded);
        Assert.Equal(["A.Tests", "N"], changed);
    }

    private static readonly IEqualityComparer<TestNode> NodeByLabel =
        EqualityComparer<TestNode>.Create((a, b) => a?.Label == b?.Label, n => n.Label.GetHashCode(StringComparison.Ordinal));
}
