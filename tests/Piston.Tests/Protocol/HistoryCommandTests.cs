// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Nodes;
using Piston.Engine.Models;
using Piston.Hosting.Protocol;
using Piston.Protocol.JsonRpc;
using Piston.Tests.Mcp;
using Xunit;

namespace Piston.Tests.Protocol;

public sealed class HistoryCommandTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static StoredRun Run(string id, params StoredTestResult[] results) => new(
        new StoredRunSummary(id, TestRunTrigger.FileChange, TestRunStatus.Completed, TestRunScope.Selective, IsFullScope: false,
            ResultFreshness.Current, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(3), "abc123", 1, 1, 0),
        results);

    [Fact]
    public async Task GetRuns_WithHistoryOff_ReportsDisabled()
    {
        var dispatcher = new EngineCommandDispatcher(new StubEngine());

        var result = await dispatcher.HandleCommandAsync(ProtocolMethods.HistoryGetRuns, null, _ct);

        Assert.False(result!["enabled"]!.GetValue<bool>());
        Assert.Empty(result["runs"]!.AsArray());
    }

    [Fact]
    public async Task GetRuns_ReturnsSummariesWithEnumsAsStrings()
    {
        var engine = new StubEngine { StoredRuns = [Run("r2"), Run("r1")] };
        var dispatcher = new EngineCommandDispatcher(engine);

        var result = await dispatcher.HandleCommandAsync(ProtocolMethods.HistoryGetRuns, null, _ct);

        Assert.True(result!["enabled"]!.GetValue<bool>());
        var runs = result["runs"]!.AsArray();
        Assert.Equal(["r2", "r1"], runs.Select(r => r!["runId"]!.GetValue<string>()));
        Assert.Equal("FileChange", runs[0]!["trigger"]!.GetValue<string>());
        Assert.Equal("Selective", runs[0]!["scope"]!.GetValue<string>());
        Assert.Equal("Current", runs[0]!["freshness"]!.GetValue<string>());
        Assert.Equal("abc123", runs[0]!["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetRun_ReturnsPerTestResults()
    {
        var engine = new StubEngine
        {
            StoredRuns = [Run("r1",
                new StoredTestResult("A.Tests", "A.Passes", TestStatus.Passed, 12, null, null),
                new StoredTestResult("A.Tests", "A.Fails", TestStatus.Failed, 30, "boom", "at A.Fails()"))],
        };
        var dispatcher = new EngineCommandDispatcher(engine);

        var result = await dispatcher.HandleCommandAsync(ProtocolMethods.HistoryGetRun, new JsonObject { ["runId"] = "r1" }, _ct);

        Assert.Equal("r1", result!["summary"]!["runId"]!.GetValue<string>());
        var results = result["results"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.Equal("Failed", results[1]!["status"]!.GetValue<string>());
        Assert.Equal("boom", results[1]!["errorMessage"]!.GetValue<string>());
        Assert.Equal(30, results[1]!["durationMs"]!.GetValue<double>());
    }

    [Fact]
    public async Task GetRun_UnknownOrMissingId_ReturnsNull()
    {
        var dispatcher = new EngineCommandDispatcher(new StubEngine { StoredRuns = [Run("r1")] });

        Assert.Null(await dispatcher.HandleCommandAsync(ProtocolMethods.HistoryGetRun, new JsonObject { ["runId"] = "nope" }, _ct));
        Assert.Null(await dispatcher.HandleCommandAsync(ProtocolMethods.HistoryGetRun, null, _ct));
    }
}
