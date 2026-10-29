// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class ChangeDiffModelTests
{
    private static readonly ChangedLineTotalsDto NoTotals = new(0, 0, 0, 0, 0, 0);

    private static DiffLineDto Added(int line, string? verdict, string text = "x") =>
        new("added", null, line, text, verdict);

    private static ChangeDiffFileDto File(string path, params DiffHunkDto[] hunks) =>
        new(path, path, "modified", IsCode: true, IsMeasured: true, IsStale: false, IsPartial: false, NoTotals, hunks, HunksOmitted: false);

    private static DiffHunkDto Hunk(params DiffLineDto[] lines) => new(1, lines.Length, 1, lines.Length, lines);

    [Theory]
    [InlineData("added", "uncovered", true)]
    [InlineData("added", "coveredOnlyByFailing", true)]
    [InlineData("added", "unknown", true)]
    [InlineData("added", "coveredByPassing", false)]
    [InlineData("added", "nonExecutable", false)]
    [InlineData("added", null, false)]
    [InlineData("context", "uncovered", false)]
    public void OnlyAddedLinesNoPassingTestProvesAreUnproven(string kind, string? verdict, bool expected) =>
        Assert.Equal(expected, ChangeDiffModel.IsUnproven(new DiffLineDto(kind, null, 1, "x", verdict)));

    [Fact]
    public void UnprovenLinesFollowFileAndLineOrderAndOnlyUnprovenHunksAreVisible()
    {
        var proven = Hunk(Added(1, "coveredByPassing"), new DiffLineDto("context", 1, 2, "c"));
        var unproven = Hunk(Added(5, "uncovered"), Added(6, "unknown"));
        var files = new[] { File("A.cs", proven, unproven), File("B.cs", Hunk(Added(2, "coveredOnlyByFailing"))) };

        Assert.Equal(
            [new ChangeLineRef("A.cs", 5), new ChangeLineRef("A.cs", 6), new ChangeLineRef("B.cs", 2)],
            ChangeDiffModel.UnprovenLines(files));
        Assert.Equal([unproven], ChangeDiffModel.VisibleHunks(files[0], onlyUnproven: true));
        Assert.Equal([proven, unproven], ChangeDiffModel.VisibleHunks(files[0], onlyUnproven: false));
    }

    [Fact]
    public void HasUnprovenUsesTheTotalsSoFilesWithoutHunksStillCount()
    {
        var file = File("A.cs") with { Totals = new ChangedLineTotalsDto(3, 2, 0, 0, 0, 1), HunksOmitted = true };

        Assert.True(ChangeDiffModel.HasUnproven(file));
        Assert.False(ChangeDiffModel.HasUnproven(file with { Totals = new ChangedLineTotalsDto(3, 2, 0, 0, 1, 0) }));
    }

    [Fact]
    public void StepWrapsAroundAndFallsBackToTheNearestLineWhenTheCurrentOneIsGone()
    {
        string[] order = ["A.cs", "B.cs"];
        ChangeLineRef[] lines = [new("A.cs", 5), new("A.cs", 9), new("B.cs", 2)];

        Assert.Equal(lines[0], ChangeDiffModel.Step(lines, null, forward: true, order));
        Assert.Equal(lines[2], ChangeDiffModel.Step(lines, null, forward: false, order));
        Assert.Equal(lines[1], ChangeDiffModel.Step(lines, lines[0], forward: true, order));
        Assert.Equal(lines[0], ChangeDiffModel.Step(lines, lines[2], forward: true, order));
        Assert.Equal(lines[2], ChangeDiffModel.Step(lines, lines[0], forward: false, order));
        Assert.Equal(lines[1], ChangeDiffModel.Step(lines, new("A.cs", 7), forward: true, order));
        Assert.Equal(lines[0], ChangeDiffModel.Step(lines, new("A.cs", 7), forward: false, order));
        Assert.Equal(lines[0], ChangeDiffModel.Step(lines, new("B.cs", 50), forward: true, order));
        Assert.Null(ChangeDiffModel.Step([], lines[0], forward: true, order));
    }

    [Fact]
    public void TestsForLineSplitsCreditedFromOutdatedAndOrdersByPrecision()
    {
        CoveringTestDto Test(string name, string granularity, bool current, int start, int end) =>
            new(name, "/t/T.csproj", granularity, [new LineRangeDto(start, end)], current);
        CoveringTestDto[] tests =
        [
            Test("Z.Run", "run", true, 1, 100),
            Test("B.Test", "test", true, 4, 6),
            Test("A.Class", "class", true, 1, 10),
            Test("A.Test", "test", true, 5, 5),
            Test("Old.Test", "test", false, 5, 5),
            Test("Elsewhere", "test", true, 20, 30),
        ];

        var result = ChangeDiffModel.TestsForLine(tests, 5);

        Assert.Equal(["A.Test", "B.Test", "A.Class", "Z.Run"], result.Credited.Select(t => t.TestFqn));
        Assert.Equal(["Old.Test"], result.Outdated.Select(t => t.TestFqn));
        Assert.Empty(ChangeDiffModel.TestsForLine(tests, 200).Credited);
    }

    [Fact]
    public void HighlightKeepsOneTokenListPerLineAndHighlightsEachSideSeparately()
    {
        var hunk = Hunk(
            new DiffLineDto("context", 1, 1, "/* start"),
            new DiffLineDto("removed", 2, null, "end */ var a = 1;"),
            new DiffLineDto("added", null, 2, "still comment */ var b = 2;"));

        var tokens = ChangeDiffModel.Highlight(hunk);

        Assert.Equal(3, tokens.Count);
        Assert.Equal(hunk.Lines.Select(l => l.Text), tokens.Select(t => string.Concat(t.Select(s => s.Text))));
        Assert.Equal(SourceTokenKind.Comment, tokens[1][0].Kind);
        Assert.Contains(tokens[1], t => t.Kind == SourceTokenKind.Keyword && t.Text == "var");
        Assert.Contains(tokens[2], t => t.Kind == SourceTokenKind.Keyword && t.Text == "var");
    }

    [Theory]
    [InlineData("working", "main", "working", null)]
    [InlineData("branch", " main ", "branch", "main")]
    [InlineData("branch", "", "branch", null)]
    [InlineData("range", "HEAD~2..HEAD", "range", "HEAD~2..HEAD")]
    [InlineData("staged", "x", "working", null)]
    [InlineData(null, null, "working", null)]
    public void RequestSendsARefOnlyForBranchAndRange(string? @base, string? baseRef, string expectedBase, string? expectedRef) =>
        Assert.Equal((expectedBase, expectedRef), ChangeDiffModel.Request(@base, baseRef));

    [Fact]
    public void EveryVerdictHasADistinctStyleAndLinesWithoutOneAreUnmarked()
    {
        var styles = ChangeDiffModel.Verdicts.Select(ChangeDiffModel.Style).ToList();

        Assert.Equal(styles.Count, styles.Select(s => s.Glyph).Distinct().Count());
        Assert.All(styles, s => Assert.NotEmpty(s.Glyph));
        Assert.Equal("", ChangeDiffModel.Style(null).Glyph);
        Assert.Equal("", ChangeDiffModel.Style(null).RowClass);
    }

    [Fact]
    public void ParsesADeepLinkAndIgnoresInvalidValues()
    {
        var link = DashboardLink.Parse("?pistonCanvas=1&owner=a%3Ab&tab=Coverage&mode=changes&base=range&ref=HEAD%7E3..HEAD");

        Assert.Equal(new DashboardLink("coverage", CoverageMode.Changes, "range", "HEAD~3..HEAD"), link);
        Assert.Equal(
            new DashboardLink(null, null, null, null),
            DashboardLink.Parse("tab=cov%20erage&mode=1&base=staged&ref=" + new string('x', 201)));
        Assert.Equal(new DashboardLink(null, null, null, null), DashboardLink.Parse("ref=a%0Ab&mode=%ZZ"));
        Assert.Equal(new DashboardLink(null, null, null, null), DashboardLink.Parse(null));
    }

    [Fact]
    public void ApplyingALinkSetsTheChangesView()
    {
        var view = new ViewState();

        view.ApplyLink(DashboardLink.Parse("mode=changes&ref=main"));
        Assert.Equal((CoverageMode.Changes, "branch", "main"), (view.CoverageMode, view.ChangeBase, view.ChangeBaseRef));

        view.ApplyLink(DashboardLink.Parse("base=working&ref=main"));
        Assert.Equal(("working", (string?)null), (view.ChangeBase, view.ChangeBaseRef));

        view.ApplyLink(DashboardLink.Parse("tab=tests"));
        Assert.Equal((CoverageMode.Changes, "working"), (view.CoverageMode, view.ChangeBase));
    }
}
