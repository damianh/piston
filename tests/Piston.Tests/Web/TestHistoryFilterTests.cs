// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class TestHistoryFilterTests
{
    private static TestResultDto Result(string fqn, TestStatusDto status) => new(fqn, fqn, status, 10, null, null, null, null);

    private static readonly TestSuiteDto Suite = new("A.Tests", [
        Result("A.Tests.C.Broke", TestStatusDto.Failed),
        Result("A.Tests.C.Wobbles", TestStatusDto.Passed),
        Result("A.Tests.C.OldFailure", TestStatusDto.Failed),
        Result("A.Tests.C.Fine", TestStatusDto.Passed),
    ], DateTimeOffset.UnixEpoch, 40);

    private static ViewState View(bool onlyNew, bool onlyFlaky) => new()
    {
        Grouping = GroupingMode.Flat,
        OnlyNewFailures = onlyNew,
        OnlyFlaky = onlyFlaky,
        TestMarks = new Dictionary<(string Project, string Test), TestMarkDto>
        {
            [("A.Tests", "A.Tests.C.Broke")] = new("A.Tests", "A.Tests.C.Broke", "New", "Passed in run r1.", Flaky: false),
            [("A.Tests", "A.Tests.C.Wobbles")] = new("A.Tests", "A.Tests.C.Wobbles", null, null, Flaky: true),
            [("A.Tests", "A.Tests.C.OldFailure")] = new("A.Tests", "A.Tests.C.OldFailure", "PreExisting", "Failed before.", Flaky: false),
        },
    };

    private static List<string> Visible(ViewState view) =>
        [.. Leaves(TestTreeBuilder.Build([Suite], null, view, null)).Select(n => n.Test!.FullyQualifiedName).Order(StringComparer.Ordinal)];

    private static IEnumerable<TestNode> Leaves(IEnumerable<TestNode> nodes) =>
        nodes.SelectMany(n => n.Test is not null ? [n] : Leaves(n.Children));

    [Theory]
    [InlineData(false, false, "A.Tests.C.Broke,A.Tests.C.Fine,A.Tests.C.OldFailure,A.Tests.C.Wobbles")]
    [InlineData(true, false, "A.Tests.C.Broke")]
    [InlineData(false, true, "A.Tests.C.Wobbles")]
    [InlineData(true, true, "A.Tests.C.Broke,A.Tests.C.Wobbles")]
    public void HistoryFiltersKeepNewFailuresFlakyTestsOrEither(bool onlyNew, bool onlyFlaky, string expected) =>
        Assert.Equal(expected.Split(','), Visible(View(onlyNew, onlyFlaky)));

    [Theory]
    [InlineData(GroupingMode.ProjectNamespaceClass)]
    [InlineData(GroupingMode.ByStatus)]
    public void HistoryFiltersApplyInEveryGrouping(GroupingMode grouping)
    {
        var view = View(onlyNew: true, onlyFlaky: false);
        view.Grouping = grouping;

        Assert.Equal(["A.Tests.C.Broke"], Visible(view));
    }

    [Fact]
    public void ANewMarkOnAPassingTestIsNotANewFailure()
    {
        var view = View(false, false);

        Assert.False(TestTreeBuilder.IsNewFailure(Result("A.Tests.C.Broke", TestStatusDto.Passed), "A.Tests", view));
        Assert.True(TestTreeBuilder.IsNewFailure(Result("A.Tests.C.Broke", TestStatusDto.Failed), "A.Tests", view));
    }

    [Theory]
    [InlineData(GroupingMode.ProjectNamespaceClass)]
    [InlineData(GroupingMode.ByStatus)]
    [InlineData(GroupingMode.Flat)]
    public void HistoryMarksStayWithinTheirProjectInEveryGrouping(GroupingMode grouping)
    {
        var test = Result("Shared.Test", TestStatusDto.Failed);
        var suites = new[]
        {
            new TestSuiteDto("A.Tests", [test], DateTimeOffset.UnixEpoch, 10),
            new TestSuiteDto("B.Tests", [test], DateTimeOffset.UnixEpoch, 10),
        };
        var view = new ViewState
        {
            Grouping = grouping,
            OnlyNewFailures = true,
            TestMarks = new Dictionary<(string Project, string Test), TestMarkDto>
            {
                [("A.Tests", test.Key)] = new("A.Tests", test.Key, "New", null, Flaky: false),
                [("B.Tests", test.Key)] = new("B.Tests", test.Key, "PreExisting", null, Flaky: true),
            },
        };

        var visible = Assert.Single(Leaves(TestTreeBuilder.Build(suites, null, view, null)));
        Assert.Equal("A.Tests", visible.Project);
        Assert.True(TestTreeBuilder.IsNewFailure(visible.Test!, visible.Project, view));
        Assert.False(TestTreeBuilder.IsFlaky(visible.Test!, visible.Project, view));

        view.OnlyNewFailures = false;
        view.OnlyFlaky = true;
        var flaky = Assert.Single(Leaves(TestTreeBuilder.Build(suites, null, view, null)));
        Assert.Equal("B.Tests", flaky.Project);

        _ = view.PinnedTestFqns.Add(test.FullyQualifiedName);
        var pinned = TestTreeBuilder.Build(suites, null, view, null).Single(n => n.Structure == NodeStructure.Pinned);
        Assert.Equal(["A.Tests", "B.Tests"], pinned.Children.Select(n => n.Project));
        Assert.False(TestTreeBuilder.IsFlaky(pinned.Children[0].Test!, pinned.Children[0].Project, view));
        Assert.True(TestTreeBuilder.IsFlaky(pinned.Children[1].Test!, pinned.Children[1].Project, view));
    }

    [Fact]
    public void SparklinePlotsDurationsOfPassesAndFailuresWithTheLongestAtTheTop()
    {
        TestHistoryEntryDto Entry(string status, double ms) => new("r", DateTimeOffset.UnixEpoch, status, ms, null);

        var points = TestHistoryStrip.SparklinePoints(
            [Entry("Passed", 10), Entry("Skipped", 0), Entry("Failed", 30), Entry("Passed", 20)], width: 100, height: 10);

        Assert.Equal("0,10 50,0 100,5", points);
    }

    [Fact]
    public void SparklineNeedsTwoTimedResultsAndDrawsAFlatHistoryMidway()
    {
        TestHistoryEntryDto Entry(double ms) => new("r", DateTimeOffset.UnixEpoch, "Passed", ms, null);

        Assert.Null(TestHistoryStrip.SparklinePoints([Entry(5)], 100, 10));
        Assert.Equal("0,5 100,5", TestHistoryStrip.SparklinePoints([Entry(5), Entry(5)], 100, 10));
    }
}
