// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Hosting.Services;
using Piston.Protocol.Messages;
using Xunit;

namespace Piston.Tests.Hosting;

public sealed class ActivityHistoryTests
{
    [Fact]
    public void Snapshot_ReturnsEventsOldestFirst_AndDropsOldestBeyondCapacity()
    {
        var history = new ActivityHistory(capacity: 2);
        var first = ActivityEventFactory.Reconnect("a");
        var second = ActivityEventFactory.Reconnect("b");
        var third = ActivityEventFactory.Reconnect("c");

        history.Add(first);
        history.Add(second);
        history.Add(third);

        Assert.Equal([second, third], history.Snapshot());
    }
}
