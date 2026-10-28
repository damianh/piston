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
        Assert.True(entry.InRunHistory);
    }
}
