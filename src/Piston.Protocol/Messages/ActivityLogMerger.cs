// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Combines retained activity history with locally received events.</summary>
public static class ActivityLogMerger
{
    /// <summary>
    /// Merges <paramref name="history"/> with <paramref name="local"/> in chronological order,
    /// dropping replayed copies by <see cref="ActivityEvent.Id"/> and keeping the newest
    /// <paramref name="capacity"/> events. Events without an ID are never collapsed.
    /// </summary>
    public static List<ActivityEvent> Merge(
        IEnumerable<ActivityEvent> history,
        IEnumerable<ActivityEvent> local,
        int capacity)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return
        [
            .. history
                .Concat(local)
                .Where(e => e.Id is null || seen.Add(e.Id))
                .OrderBy(e => e.Timestamp)
                .TakeLast(capacity),
        ];
    }
}
