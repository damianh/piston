// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class SessionTimelineTests
{
    private static readonly TestReference Broken = new("A.Tests", "A.Broken");

    private static SessionTimelineEntryDto Entry(
        long sequence,
        string? before,
        string? after = null,
        string[]? files = null,
        string status = "Completed",
        string freshness = "Current",
        TestReference[]? newlyFailing = null,
        TestReference[]? fixedTests = null,
        FailureClassificationDto[]? classifications = null,
        int unlistedNewlyFailing = 0,
        int unlistedFixed = 0,
        bool noEarlierResults = false)
    {
        var at = DateTimeOffset.UnixEpoch.AddMinutes(sequence);
        return new SessionTimelineEntryDto(
            $"r{sequence}", sequence, files is null ? "Forced" : "FileChange", status, null, "Full", true, false, freshness, null,
            at, before is null ? null : at, at.AddSeconds(5), before, after ?? before, false, files ?? [], files?.Length ?? 0,
            "Succeeded", 0, 1, newlyFailing?.Length ?? 0, 0,
            [.. (newlyFailing ?? []).Select(t => new TestOutcomeReferenceDto(t.Project, t.Key))], (newlyFailing?.Length ?? 0) + unlistedNewlyFailing,
            [.. (fixedTests ?? []).Select(t => new TestOutcomeReferenceDto(t.Project, t.Key))], (fixedTests?.Length ?? 0) + unlistedFixed,
            classifications ?? [], false, noEarlierResults);
    }

    [Fact]
    public void Build_InsertsAnEditBeforeEachRunThatTestedChangedSource()
    {
        var timeline = SessionTimeline.Build(
        [
            Entry(3, "bbb", files: ["/src/B.cs"]),
            Entry(1, "aaa"),
            Entry(2, "aaa"),
        ]);

        Assert.Collection(timeline.Items,
            i => Assert.Equal("r1", Assert.IsType<SessionTimelineRun>(i).Entry.RunId),
            i => Assert.Equal("r2", Assert.IsType<SessionTimelineRun>(i).Entry.RunId),
            i =>
            {
                var edit = Assert.IsType<SessionTimelineEdit>(i);
                Assert.Equal(("aaa", "bbb", "r3"), (edit.FromSource, edit.ToSource, edit.NextRunId));
                Assert.Equal(["/src/B.cs"], edit.Files);
            },
            i => Assert.Equal(3, Assert.IsType<SessionTimelineRun>(i).Number));
    }

    [Fact]
    public void Build_CarriesFilesOfRunsSupersededBeforeTheyStartedToTheNextEdit()
    {
        var timeline = SessionTimeline.Build(
        [
            Entry(1, "aaa"),
            Entry(2, null, files: ["/src/A.cs"], status: "Cancelled", freshness: "Unknown"),
            Entry(3, "ccc", files: ["/src/B.cs", "/src/A.cs"]),
        ]);

        var edit = Assert.Single(timeline.Items.OfType<SessionTimelineEdit>());
        Assert.Equal(["/src/A.cs", "/src/B.cs"], edit.Files);
        Assert.Equal(2, edit.FileCount);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(2), edit.At);
        Assert.False(timeline.Runs.Single(r => r.Entry.RunId == "r2").Started);
    }

    [Fact]
    public void Build_MarksRunsWhoseSourceChangedWhileTheyRan()
    {
        var timeline = SessionTimeline.Build([Entry(1, "aaa", after: "bbb", freshness: "Stale"), Entry(2, "bbb", files: ["/src/A.cs"])]);

        Assert.True(timeline.Runs.First().EditedDuringRun);
        Assert.False(timeline.Runs.Last().EditedDuringRun);
        var edit = Assert.Single(timeline.Items.OfType<SessionTimelineEdit>());
        Assert.Equal(("aaa", "bbb"), (edit.FromSource, edit.ToSource));
    }

    [Fact]
    public void TraceFailures_FindsTheRunAndEditThatStartedTheCurrentFailingStreak()
    {
        var newKind = new FailureClassificationDto("A.Tests", "A.Broken", "New", "Passed in run r3.");
        var timeline = SessionTimeline.Build(
        [
            Entry(1, "aaa", newlyFailing: [Broken]),
            Entry(2, "bbb", files: ["/src/Fix.cs"], fixedTests: [Broken]),
            Entry(3, "ccc", files: ["/src/Break.cs"]),
            Entry(4, "ccc", newlyFailing: [Broken], classifications: [newKind]),
            Entry(5, "ddd", files: ["/src/Other.cs"]),
        ]);

        var trace = Assert.Single(timeline.TraceFailures([Broken]));

        Assert.Equal(FailureOrigin.Run, trace.Origin);
        Assert.Equal("r4", trace.IntroducedBy!.Entry.RunId);
        Assert.Equal(["/src/Break.cs"], trace.Edit!.Files);
        Assert.Equal("r3", trace.Edit.NextRunId);
        Assert.Equal(newKind, trace.Classification);
    }

    [Fact]
    public void Build_NumbersRunsFromTheSessionsStart_WhenEarlierRunsWereDropped()
    {
        var timeline = SessionTimeline.Build([Entry(501, "aaa"), Entry(502, "bbb", files: ["/src/A.cs"])], earlierRunsDropped: 500);

        Assert.Equal([501, 502], timeline.Runs.Select(r => r.Number));
    }

    [Fact]
    public void TraceFailures_WhenTheIntroducingRunTestedTheStartingSource_ReportsUnchangedSource()
    {
        var timeline = SessionTimeline.Build([Entry(1, "aaa"), Entry(2, "aaa", newlyFailing: [Broken])]);

        var trace = Assert.Single(timeline.TraceFailures([Broken]));

        Assert.Equal("r2", trace.IntroducedBy!.Entry.RunId);
        Assert.Null(trace.Edit);
        Assert.True(trace.SourceUnchanged);
    }

    [Fact]
    public void TraceFailures_WhenTheIntroducingRunsSourceIsUnknown_DoesNotClaimUnchangedSource()
    {
        var timeline = SessionTimeline.Build([Entry(1, "aaa"), Entry(2, null, newlyFailing: [Broken])]);

        var trace = Assert.Single(timeline.TraceFailures([Broken]));

        Assert.Equal("r2", trace.IntroducedBy!.Entry.RunId);
        Assert.Null(trace.Edit);
        Assert.False(trace.SourceUnchanged);
    }

    [Fact]
    public void TraceFailures_WhenResultsWereClearedBeforeTheFailure_ReportsAnUnknownOrigin()
    {
        var timeline = SessionTimeline.Build(
        [
            Entry(1, "aaa", noEarlierResults: true),
            Entry(2, "bbb", files: ["/src/Break.cs"], noEarlierResults: true),
        ]);

        var trace = Assert.Single(timeline.TraceFailures([Broken]));

        Assert.Equal(FailureOrigin.Unknown, trace.Origin);
    }

    [Fact]
    public void TraceFailures_WhenResultsWereClearedAfterTheIntroduction_KeepsTheIntroducingRun()
    {
        var timeline = SessionTimeline.Build(
        [
            Entry(1, "aaa", noEarlierResults: true),
            Entry(2, "bbb", files: ["/src/Break.cs"], newlyFailing: [Broken]),
            Entry(3, "bbb", noEarlierResults: true),
        ]);

        var trace = Assert.Single(timeline.TraceFailures([Broken]));

        Assert.Equal(FailureOrigin.Run, trace.Origin);
        Assert.Equal("r2", trace.IntroducedBy!.Entry.RunId);
    }

    [Fact]
    public void TraceFailures_ForTheSessionsFirstResults_ReportsTheSessionStart()
    {
        var timeline = SessionTimeline.Build([Entry(1, "aaa", noEarlierResults: true), Entry(2, "bbb", files: ["/src/A.cs"])]);

        Assert.Equal(FailureOrigin.SessionStart, Assert.Single(timeline.TraceFailures([Broken])).Origin);
    }

    [Fact]
    public void TraceFailures_WithoutANewFailureInTheSession_ReportsNoIntroducingRun()
    {
        var preExisting = new FailureClassificationDto("A.Tests", "A.Broken", "PreExisting", "Also failed in 3 earlier run(s).");
        var timeline = SessionTimeline.Build([Entry(1, "aaa", classifications: [preExisting]), Entry(2, "bbb", files: ["/src/A.cs"])]);

        var trace = Assert.Single(timeline.TraceFailures([Broken]));

        Assert.Equal(FailureOrigin.SessionStart, trace.Origin);
        Assert.Null(trace.IntroducedBy);
        Assert.Null(trace.Edit);
        Assert.Equal(preExisting, trace.Classification);
    }

    [Fact]
    public void TraceFailures_WhenEarlierRunsWereDropped_ReportsAnUnknownOrigin()
    {
        var timeline = SessionTimeline.Build([Entry(501, "aaa"), Entry(502, "bbb", files: ["/src/A.cs"])], earlierRunsDropped: 500);

        var trace = Assert.Single(timeline.TraceFailures([Broken]));

        Assert.Equal(FailureOrigin.Unknown, trace.Origin);
        Assert.Null(trace.IntroducedBy);
    }

    [Fact]
    public void TraceFailures_WhenATruncatedListMayHoldTheIntroduction_ReportsAnUnknownOrigin()
    {
        var timeline = SessionTimeline.Build([Entry(1, "aaa"), Entry(2, "bbb", files: ["/src/A.cs"], unlistedNewlyFailing: 300)]);

        var trace = Assert.Single(timeline.TraceFailures([Broken]));

        Assert.Equal(FailureOrigin.Unknown, trace.Origin);
    }

    [Fact]
    public void TraceFailures_WhenTruncatedListsMayHoldALaterFixAndReintroduction_ReportsAnUnknownOrigin()
    {
        var timeline = SessionTimeline.Build(
        [
            Entry(1, "aaa"),
            Entry(2, "bbb", files: ["/src/Break.cs"], newlyFailing: [Broken]),
            Entry(3, "ccc", files: ["/src/MassFix.cs"], unlistedFixed: 300),
            Entry(4, "ddd", files: ["/src/MassBreak.cs"], unlistedNewlyFailing: 300),
        ]);

        var trace = Assert.Single(timeline.TraceFailures([Broken]));

        Assert.Equal(FailureOrigin.Unknown, trace.Origin);
        Assert.Null(trace.IntroducedBy);
        Assert.Null(trace.Edit);
    }

    [Fact]
    public void TraceFailures_WhenOnlyATruncatedFixedListFollows_KeepsTheIntroducingRun()
    {
        // The test is still failing, so an unlisted fix would need a later new failure, and none could be unlisted.
        var timeline = SessionTimeline.Build(
        [
            Entry(1, "aaa"),
            Entry(2, "bbb", files: ["/src/Break.cs"], newlyFailing: [Broken]),
            Entry(3, "ccc", files: ["/src/MassFix.cs"], unlistedFixed: 300),
            Entry(4, "ddd", files: ["/src/Other.cs"]),
        ]);

        var trace = Assert.Single(timeline.TraceFailures([Broken]));

        Assert.Equal(FailureOrigin.Run, trace.Origin);
        Assert.Equal("r2", trace.IntroducedBy!.Entry.RunId);
    }

    [Fact]
    public void Related_HighlightsRunsThatChangedTheTestsOutcomeAndTheirEdits()
    {
        var timeline = SessionTimeline.Build(
        [
            Entry(1, "aaa"),
            Entry(2, "bbb", files: ["/src/Break.cs"], newlyFailing: [Broken]),
            Entry(3, "ccc", files: ["/src/Other.cs"]),
            Entry(4, "ddd", files: ["/src/Fix.cs"], fixedTests: [Broken]),
        ]);

        var (runs, edits) = timeline.Related(Broken);

        Assert.Equal(["r2", "r4"], runs.Order(StringComparer.Ordinal));
        Assert.Equal(["r2", "r4"], edits.Select(e => e.NextRunId).Order(StringComparer.Ordinal));
    }
}
