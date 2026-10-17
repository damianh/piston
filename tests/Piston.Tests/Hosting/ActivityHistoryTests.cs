// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Hosting.Services;
using Piston.Protocol.JsonRpc;
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

    [Fact]
    public void Snapshot_KeepsTheNewestEventsThatFitTheByteBudget()
    {
        var history = new ActivityHistory();
        var failures = Enumerable.Range(0, 10)
            .Select(i => new TestFailureData($"Tests.T{i}", new string('"', 8 * 1024)))
            .ToList();
        for (var i = 0; i < 250; i++)
        {
            history.Add(ActivityEventFactory.TestRunCompleted("/repo/All.slnx", 0, 10, 0, i, failures));
        }

        var latest = ActivityEventFactory.Reconnect("latest");
        history.Add(latest);

        var snapshot = history.Snapshot();
        var response = JsonSerializer.SerializeToUtf8Bytes(new ActivityLogResponse(snapshot), JsonRpcSerializer.Options);

        Assert.InRange(response.LongLength, 1, ActivityHistory.DefaultByteBudget + 64);
        Assert.InRange(snapshot.Count, 2, 250);
        Assert.Same(latest, snapshot[^1]);
        Assert.Equal(249, snapshot[^2].Data!["durationMs"]!.GetValue<double>());
    }

    [Fact]
    public void Snapshot_OmitsOlderEvents_OnceAnEventDoesNotFit()
    {
        var small = ActivityEventFactory.Reconnect("small");
        var smallSize = JsonSerializer.SerializeToUtf8Bytes(small, JsonRpcSerializer.Options).LongLength + 1;
        var history = new ActivityHistory(byteBudget: smallSize * 2);
        var older = ActivityEventFactory.Reconnect("older");
        var large = ActivityEventFactory.BuildCompleted("/repo/All.slnx", false, 1, [new string('x', 4096)]);

        history.Add(older);
        history.Add(large);
        history.Add(small);

        Assert.Equal([small], history.Snapshot());
    }
}
