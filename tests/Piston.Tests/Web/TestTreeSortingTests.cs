// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class TestTreeSortingTests
{
    private static TestResultDto Test(string name, double duration, TestStatusDto status = TestStatusDto.Passed) =>
        new(name, name, status, duration, null, null, null, null);

    private static TestSuiteDto Suite(string name, params TestResultDto[] tests) =>
        new(name, tests, DateTimeOffset.UnixEpoch, tests.Sum(t => t.DurationMs));

    [Fact]
    public void ColumnChangesChooseDefaultDirectionAndRepeatedClicksReverseIt()
    {
        var view = new ViewState();
        Assert.Equal((TestSort.Name, false), (view.TestSort, view.TestSortDescending));

        view.SortTestsBy(TestSort.Duration);
        Assert.Equal((TestSort.Duration, true), (view.TestSort, view.TestSortDescending));
        view.SortTestsBy(TestSort.Duration);
        Assert.Equal((TestSort.Duration, false), (view.TestSort, view.TestSortDescending));
        view.SortTestsBy(TestSort.Name);
        Assert.Equal((TestSort.Name, false), (view.TestSort, view.TestSortDescending));
        view.SortTestsBy(TestSort.Name);
        Assert.Equal((TestSort.Name, true), (view.TestSort, view.TestSortDescending));
    }

    [Theory]
    [InlineData(GroupingMode.Flat)]
    [InlineData(GroupingMode.ByStatus)]
    [InlineData(GroupingMode.ProjectNamespaceClass)]
    public void ColumnsSortTestsInBothDirectionsInEveryGrouping(GroupingMode grouping)
    {
        var view = new ViewState { Grouping = grouping };
        TestSuiteDto[] suites = [Suite("Tests", Test("N.C.Alpha", 10), Test("N.C.Beta", 50), Test("N.C.Gamma", 20))];
        string[] Names() => TestTreeBuilder.Build(suites, null, view, null)
            .SelectMany(n => n.Tests()).Select(t => t.FullyQualifiedName).ToArray();

        Assert.Equal(["N.C.Alpha", "N.C.Beta", "N.C.Gamma"], Names());
        view.SortTestsBy(TestSort.Name);
        Assert.Equal(["N.C.Gamma", "N.C.Beta", "N.C.Alpha"], Names());
        view.SortTestsBy(TestSort.Duration);
        Assert.Equal(["N.C.Beta", "N.C.Gamma", "N.C.Alpha"], Names());
        view.SortTestsBy(TestSort.Duration);
        Assert.Equal(["N.C.Alpha", "N.C.Gamma", "N.C.Beta"], Names());
    }

    [Fact]
    public void DurationSortUsesGroupTotalsAndKeepsPinnedSectionFirst()
    {
        var view = new ViewState();
        _ = view.PinnedTestFqns.Add("N.C.Pinned");
        view.SortTestsBy(TestSort.Duration);
        TestSuiteDto[] suites =
        [
            Suite("Alpha", Test("N.C.One", 40), Test("N.C.Two", 40), Test("N.C.Pinned", 0)),
            Suite("Beta", Test("N.C.Three", 60)),
        ];

        var roots = TestTreeBuilder.Build(suites, null, view, null);
        Assert.Equal(["pinned", "suite:Alpha", "suite:Beta"], roots.Select(n => n.ExpansionKey));
        view.SortTestsBy(TestSort.Duration);
        roots = TestTreeBuilder.Build(suites, null, view, null);
        Assert.Equal(["pinned", "suite:Beta", "suite:Alpha"], roots.Select(n => n.ExpansionKey));
    }

    [Fact]
    public void DuplicateShortNamesHaveSeparateNodesAndDisambiguatedLabels()
    {
        TestSuiteDto[] suites =
        [
            Suite("Tests", Test("N.C.One", 1)) with { ProjectId = "One/Tests.csproj" },
            Suite("Tests", Test("N.C.Two", 1)) with { ProjectId = "Two/Tests.csproj" },
        ];

        var roots = TestTreeBuilder.Build(suites, null, new ViewState(), null);

        Assert.Equal(["suite:One/Tests.csproj", "suite:Two/Tests.csproj"], roots.Select(node => node.ExpansionKey));
        Assert.Equal(["Tests (One/Tests.csproj)", "Tests (Two/Tests.csproj)"], roots.Select(node => node.Label));
        var detail = NodeDetail.For(roots[1].ExpansionKey!, suites, suites, roots);
        Assert.Equal("Tests (Two/Tests.csproj)", detail!.Title);
        Assert.Equal(["Two/Tests.csproj"], detail.Run!.ProjectIds);
    }

    [Fact]
    public void LiveOverlayPreservesSharedTestsInOtherProjects()
    {
        var first = Suite("Tests", Test("Shared.Test", 1)) with { ProjectId = "One/Tests.csproj" };
        var second = first with { ProjectId = "Two/Tests.csproj" };
        var live = first with { Tests = [Test("Shared.Test", 0, TestStatusDto.Running)] };

        var overlay = TestTreeBuilder.WithLiveOverlay([first, second], [live]);
        var roots = TestTreeBuilder.Build(overlay, null, new ViewState(), null);

        Assert.Equal(["suite:One/Tests.csproj", "suite:Two/Tests.csproj"], roots.Select(n => n.ExpansionKey));
        Assert.Equal([TestStatusDto.Running, TestStatusDto.Passed], roots.Select(n => Assert.Single(n.Tests()).Status));
        var view = new ViewState { SelectedTestProject = second.ProjectId, SelectedTestKey = "Shared.Test" };
        Assert.Same(second.Tests[0], view.FindSelectedTest(overlay));
    }

    [Fact]
    public void DurationSortOrdersNestedGroupsAndIgnoresRunningDurations()
    {
        var view = new ViewState();
        view.SortTestsBy(TestSort.Duration);
        var suites = new[]
        {
            Suite("Tests", Test("A.C.One", 40), Test("B.C.Two", 60), Test("A.C.Running", 1000, TestStatusDto.Running)),
        };

        var root = Assert.Single(TestTreeBuilder.Build(suites, null, view, null));
        Assert.Equal(["B", "A"], root.Children.Select(n => n.Label));
        var runningGroup = root.Children[1].Children[0];
        Assert.Equal(["One", "Running"], runningGroup.Children.Select(n => n.Label));
    }

    [Fact]
    public void StatusGroupsKeepFailureFirstWhileChildrenSort()
    {
        var view = new ViewState { Grouping = GroupingMode.ByStatus };
        view.SortTestsBy(TestSort.Duration);
        var roots = TestTreeBuilder.Build(
            [Suite("Tests", Test("N.C.Passed", 100), Test("N.C.Failed", 1, TestStatusDto.Failed))],
            null, view, null);

        Assert.Equal([TestStatusDto.Failed, TestStatusDto.Passed], roots.Select(n => Assert.Single(n.Tests()).Status));
    }
}
