// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Messages;
using Xunit;

namespace Piston.Tests.Protocol;

public sealed class ActivityLogMergerTests
{
    private static ActivityEvent Event(string? id, int seconds, string type = ActivityEventTypes.McpToolCall) =>
        new(type, DateTimeOffset.UnixEpoch.AddSeconds(seconds), null, null, id);

    [Fact]
    public void Merge_KeepsDistinctEventsSharingTypeAndTimestamp()
    {
        var a = Event("a", 1);
        var b = Event("b", 1);

        var merged = ActivityLogMerger.Merge([a, b], [], capacity: 10);

        Assert.Equal(["a", "b"], merged.Select(e => e.Id));
    }

    [Fact]
    public void Merge_DropsReplayedCopiesOfLiveEvents()
    {
        var history = new[] { Event("a", 1), Event("b", 2) };
        var local = new[] { Event("b", 2), Event("c", 3) };

        var merged = ActivityLogMerger.Merge(history, local, capacity: 10);

        Assert.Equal(["a", "b", "c"], merged.Select(e => e.Id));
    }

    [Fact]
    public void Merge_NeverCollapsesEventsWithoutId()
    {
        var merged = ActivityLogMerger.Merge([Event(null, 1)], [Event(null, 1)], capacity: 10);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_OrdersChronologicallyAndKeepsNewestWithinCapacity()
    {
        var history = new[] { Event("a", 1), Event("c", 3) };
        var local = new[] { Event("b", 2), Event("d", 4) };

        var merged = ActivityLogMerger.Merge(history, local, capacity: 3);

        Assert.Equal(["b", "c", "d"], merged.Select(e => e.Id));
    }
}
