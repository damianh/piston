// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Hosting.Services;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Xunit;

namespace Piston.Tests.Mcp;

public sealed class McpCallTrackerTests
{
    [Fact]
    public void Record_EmitsWireEvent_AndKeepsCallLog()
    {
        var events = new List<ActivityEvent>();
        var tracker = new McpCallTracker(events.Add, "/repo/Test.slnx");

        tracker.Record("run_tests", null, "passed", 12.5, true);

        var activity = Assert.Single(events);
        Assert.Equal(ActivityEventTypes.McpToolCall, activity.Type);
        Assert.Equal("/repo/Test.slnx", activity.SolutionPath);
        Assert.NotNull(activity.Data);
        var data = activity.Data.Deserialize<McpToolCallData>(JsonRpcSerializer.Options)!;
        Assert.Equal(new McpToolCallData("run_tests", null, "passed", 12.5, true), data);
        Assert.Equal(data, Assert.Single(tracker.GetCallLog()));
        Assert.Null(activity.Data["paramsSummary"]);
    }

    [Fact]
    public void Record_RetainsTheLast200Calls_InChronologicalOrder()
    {
        var events = new List<ActivityEvent>();
        var tracker = new McpCallTracker(events.Add, null);

        for (var i = 0; i < 205; i++)
        {
            tracker.Record($"Tool{i}", null, null, i, false);
        }

        var log = tracker.GetCallLog();
        Assert.Equal(200, log.Count);
        Assert.Equal(Enumerable.Range(5, 200).Select(i => $"Tool{i}"), log.Select(e => e.ToolName));
        Assert.Equal(205, events.Count);
    }
}
