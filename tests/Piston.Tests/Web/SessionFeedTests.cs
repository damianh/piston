// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Nodes;
using Piston.Protocol.Dtos;
using Piston.Protocol.Messages;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class SessionFeedTests
{
    private static DateTimeOffset At(int minute) => DateTimeOffset.UnixEpoch.AddMinutes(minute);

    private static SessionTimelineEntryDto Entry(long sequence, string source, string[]? files = null) =>
        new(
            $"r{sequence}", sequence, files is null ? "Forced" : "FileChange", "Completed", null, "Full", true, false, "Current", null,
            At((int)sequence * 10), At((int)sequence * 10), At((int)sequence * 10).AddSeconds(5), source, source, false,
            files ?? [], files?.Length ?? 0, "Succeeded", 0, 1, 0, 0, [], 0, [], 0, [], false);

    private static ActivityEvent Call(int minute, string tool = "run_tests", string? args = null, bool succeeded = true) =>
        new(ActivityEventTypes.McpToolCall, At(minute), null,
            new JsonObject { ["toolName"] = tool, ["paramsSummary"] = args, ["succeeded"] = succeeded, ["durationMs"] = 1.0 });

    private static ActivityEvent Other(string type, int minute) => new(type, At(minute), null, null);

    private static string Describe(SessionFeedRow row) => row switch
    {
        { Item: SessionTimelineRun run } => run.Entry.RunId,
        { Item: SessionTimelineEdit } => "edit",
        { Event: { } e } when SessionFeed.IsAgentCall(e) => $"{e.Data!["toolName"]}x{row.RepeatCount}",
        { Event: { } e } => e.Type,
        _ => "?",
    };

    [Fact]
    public void ForSession_DropsEventsFromEarlierSessionsAndKeepsEventsAtStart()
    {
        ActivityEvent[] events =
        [
            Call(9),
            Call(10),
            Other(ActivityEventTypes.Reconnect, 11),
        ];

        var currentSession = SessionFeed.ForSession(events, At(10));

        Assert.Equal([10, 11], currentSession.Select(e => e.Timestamp.Minute));
        Assert.Equal(1, currentSession.Count(SessionFeed.IsAgentCall));
    }

    [Fact]
    public void Build_InterleavesAgentCallsAndMarkersNewestFirstAndDropsEventsTheTimelineShows()
    {
        // r1 at 10, edit at 20 (r2's request), r2 at 20.
        var timeline = SessionTimeline.Build([Entry(1, "aaa"), Entry(2, "bbb", ["/src/A.cs"])]);
        ActivityEvent[] events =
        [
            Call(25, "get_test_results"),
            Other(ActivityEventTypes.SolutionLoaded, 1),
            Call(15, "run_tests"),
            Other(ActivityEventTypes.BuildCompleted, 12),
            Other(ActivityEventTypes.TestRunCompleted, 13),
            Other(ActivityEventTypes.FileChangesDetected, 19),
            Other(ActivityEventTypes.Reconnect, 30),
        ];

        var rows = SessionFeed.Build(timeline, events, includeAgentCalls: true);

        Assert.Equal(
            [ActivityEventTypes.Reconnect, "get_test_resultsx1", "r2", "edit", "run_testsx1", "r1", ActivityEventTypes.SolutionLoaded],
            rows.Select(Describe));
    }

    [Fact]
    public void Build_WithoutAgentCalls_KeepsTheMarkers()
    {
        var timeline = SessionTimeline.Build([Entry(1, "aaa")]);

        var rows = SessionFeed.Build(timeline, [Call(15), Other(ActivityEventTypes.Reconnect, 16)], includeAgentCalls: false);

        Assert.Equal([ActivityEventTypes.Reconnect, "r1"], rows.Select(Describe));
    }

    [Fact]
    public void Build_CollapsesIdenticalConsecutiveCallsButNotAcrossARunOrADifferentCall()
    {
        var timeline = SessionTimeline.Build([Entry(1, "aaa")]);
        ActivityEvent[] events =
        [
            Call(1, args: "a"), Call(2, args: "a"),
            Call(11, args: "a"), Call(12, args: "a"), Call(13, args: "a"),
            Call(14, args: "a", succeeded: false),
            Call(15, args: "b"),
        ];

        var rows = SessionFeed.Build(timeline, events, includeAgentCalls: true);

        Assert.Equal(["run_testsx1", "run_testsx1", "run_testsx3", "r1", "run_testsx2"], rows.Select(Describe));
        Assert.Equal(At(13), rows[2].Event!.Timestamp);
    }
}
