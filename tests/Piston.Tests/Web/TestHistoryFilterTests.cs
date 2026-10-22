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
        TestMarks = new Dictionary<string, TestMarkDto>
        {
            ["A.Tests.C.Broke"] = new("A.Tests", "A.Tests.C.Broke", "New", "Passed in run r1.", Flaky: false),
            ["A.Tests.C.Wobbles"] = new("A.Tests", "A.Tests.C.Wobbles", null, null, Flaky: true),
            ["A.Tests.C.OldFailure"] = new("A.Tests", "A.Tests.C.OldFailure", "PreExisting", "Failed before.", Flaky: false),
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

        Assert.False(TestTreeBuilder.IsNewFailure(Result("A.Tests.C.Broke", TestStatusDto.Passed), view));
        Assert.True(TestTreeBuilder.IsNewFailure(Result("A.Tests.C.Broke", TestStatusDto.Failed), view));
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
