// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Messages;

namespace Piston.Web.Models;

/// <summary>A row of the session feed: an edit or run, or an agent tool call or marker from the activity log.</summary>
/// <param name="Item">The edit or run; null for an activity row.</param>
/// <param name="Event">The newest of the activity row's events; null for an edit or run.</param>
/// <param name="RepeatCount">How many identical consecutive agent calls the row stands for.</param>
public sealed record SessionFeedRow(SessionTimelineItem? Item, ActivityEvent? Event, int RepeatCount = 1);

/// <summary>Interleaves the session's edits and runs with the activity the timeline doesn't already show.</summary>
public static class SessionFeed
{
    /// <summary>Returns activity at or after the current session's start time.</summary>
    public static IReadOnlyList<ActivityEvent> ForSession(IEnumerable<ActivityEvent> events, DateTimeOffset startedAt) =>
        events.Where(e => e.Timestamp >= startedAt).ToList();

    /// <summary>
    /// The feed, newest first. Of the activity log it keeps agent tool calls (when <paramref name="includeAgentCalls"/>)
    /// and the solution-loaded and reconnect markers; file changes, builds and test runs are the timeline's edits and
    /// runs. Edits and runs keep their order; each event goes before the first item timed after it. Consecutive
    /// identical agent calls (same tool, parameters and outcome) collapse into one row.
    /// </summary>
    public static IReadOnlyList<SessionFeedRow> Build(SessionTimeline timeline, IEnumerable<ActivityEvent> events, bool includeAgentCalls)
    {
        var kept = events
            .Where(e => e.Type is ActivityEventTypes.SolutionLoaded or ActivityEventTypes.Reconnect
                || (includeAgentCalls && IsAgentCall(e)))
            .OrderBy(e => e.Timestamp)
            .ToList();

        var merged = new List<SessionFeedRow>(timeline.Items.Count + kept.Count);
        var next = 0;
        foreach (var item in timeline.Items)
        {
            while (next < kept.Count && kept[next].Timestamp < item.At)
            {
                merged.Add(new SessionFeedRow(null, kept[next++]));
            }

            merged.Add(new SessionFeedRow(item, null));
        }

        while (next < kept.Count)
        {
            merged.Add(new SessionFeedRow(null, kept[next++]));
        }

        var rows = new List<SessionFeedRow>(merged.Count);
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var row = merged[i];
            if (rows.Count > 0 && rows[^1].Event is { } newer && row.Event is { } older && IsRepeatOf(newer, older))
            {
                rows[^1] = rows[^1] with { RepeatCount = rows[^1].RepeatCount + 1 };
                continue;
            }

            rows.Add(row);
        }

        return rows;
    }

    public static bool IsAgentCall(ActivityEvent e) => e.Type == ActivityEventTypes.McpToolCall;

    private static bool IsRepeatOf(ActivityEvent newer, ActivityEvent older) =>
        IsAgentCall(newer) && IsAgentCall(older)
        && Str(newer, "toolName") == Str(older, "toolName")
        && Str(newer, "paramsSummary") == Str(older, "paramsSummary")
        && newer.Data?["succeeded"]?.GetValue<bool>() == older.Data?["succeeded"]?.GetValue<bool>();

    private static string? Str(ActivityEvent e, string key) => e.Data?[key]?.GetValue<string>();
}
