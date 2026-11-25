// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.Models;

public sealed class SessionTimelineEntryTests
{
    [Fact]
    public void From_CapsListsButKeepsTheirTotals()
    {
        var many = Enumerable.Range(0, SessionTimelineEntry.MaxListItems + 5).ToList();
        var run = new TestRunRecord
        {
            RunId = "r1",
            EngineInstanceId = "e",
            Sequence = 7,
            Trigger = TestRunTrigger.FileChange,
            Status = TestRunStatus.BuildFailed,
            RequestedAt = DateTimeOffset.UnixEpoch,
            SourceBefore = new SourceFingerprint("piston-source-v2", "abc", 3),
            ChangedFiles = [.. many.Select(i => $"/src/F{i}.cs")],
            NewlyFailingTests = [.. many.Select(i => new TestOutcomeReference("P", $"T{i}"))],
            Build = new BuildResult(BuildStatus.Failed, ["e1", "e2"], [], TimeSpan.FromSeconds(1)) { OmittedErrors = 600 },
        };

        var entry = SessionTimelineEntry.From(run, inRunHistory: true);

        Assert.Equal(SessionTimelineEntry.MaxListItems, entry.ChangedFiles.Count);
        Assert.Equal(many.Count, entry.ChangedFileCount);
        Assert.Equal(SessionTimelineEntry.MaxListItems, entry.NewlyFailingTests.Count);
        Assert.Equal(many.Count, entry.NewlyFailingCount);
        Assert.Equal("abc", entry.SourceBefore);
        Assert.Null(entry.SourceAfter);
        Assert.Equal(BuildStatus.Failed, entry.BuildStatus);
        Assert.Equal(602, entry.BuildErrorCount);
        Assert.Equal(["e1", "e2"], entry.BuildErrors);
        Assert.True(entry.InRunHistory);
    }

    [Fact]
    public void From_KeepsTheFirstBuildErrorsTruncated()
    {
        var run = new TestRunRecord
        {
            RunId = "r1",
            EngineInstanceId = "e",
            Sequence = 1,
            Trigger = TestRunTrigger.Forced,
            Status = TestRunStatus.BuildFailed,
            RequestedAt = DateTimeOffset.UnixEpoch,
            Build = new BuildResult(
                BuildStatus.Failed,
                [new string('x', RetentionLimits.MaxActivityMessageLength * 2), .. Enumerable.Range(1, SessionTimelineEntry.MaxBuildErrors + 4).Select(i => $"e{i}")],
                [],
                TimeSpan.FromSeconds(1)),
        };

        var entry = SessionTimelineEntry.From(run, inRunHistory: false);

        Assert.Equal(SessionTimelineEntry.MaxBuildErrors, entry.BuildErrors!.Count);
        Assert.InRange(entry.BuildErrors[0].Length, 1, RetentionLimits.MaxActivityMessageLength);
        Assert.Equal("e1", entry.BuildErrors[1]);
        Assert.Equal(SessionTimelineEntry.MaxBuildErrors + 5, entry.BuildErrorCount);
    }
}
