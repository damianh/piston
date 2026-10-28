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
    public async Task GetTestMarks_CombinesLatestFailuresWithFlakyTests()
    {
        var entry = new TestHistoryEntry("r1", DateTimeOffset.UnixEpoch, TestStatus.Passed, 5, "abc");
        var engine = new StubEngine
        {
            LatestFailures =
            [
                new FailureClassification("A.Tests", "A.Broke", FailureKind.New, "Passed in run r1."),
                new FailureClassification("A.Tests", "A.Wobbles", FailureKind.Flaky, "Passed and failed on abc."),
            ],
            Histories =
            [
                new TestHistory("A.Tests", "A.Wobbles", [entry], Flakiness: new FlakinessEvidence("abc", 1, 1, 0, [], [])),
                new TestHistory("A.Tests", "A.WobblesButPassing", [entry], Flakiness: new FlakinessEvidence("abc", 1, 1, 0, [], [])),
                new TestHistory("A.Tests", "A.Steady", [entry], Flakiness: null),
            ],
        };
        var dispatcher = new EngineCommandDispatcher(engine);

        var result = await dispatcher.HandleCommandAsync(ProtocolMethods.HistoryGetTestMarks, null, _ct);

        Assert.True(result!["enabled"]!.GetValue<bool>());
        var marks = result["tests"]!.AsArray().ToDictionary(m => m!["test"]!.GetValue<string>());
        Assert.Equal(["A.Broke", "A.Wobbles", "A.WobblesButPassing"], marks.Keys);
        Assert.Equal("New", marks["A.Broke"]!["failureKind"]!.GetValue<string>());
        Assert.False(marks["A.Broke"]!["flaky"]!.GetValue<bool>());
        Assert.True(marks["A.Wobbles"]!["flaky"]!.GetValue<bool>());
        Assert.Null(marks["A.WobblesButPassing"]!["failureKind"]);
    }

    [Fact]
    public async Task GetTestMarks_CarryFlakyEvidence()
    {
        var at = DateTimeOffset.UnixEpoch;
        var failingEvidence = new FlakinessEvidence(null, 1, 1, 1, [], [new("r2", at, TestStatus.Failed, 0, null), new("r2", at, TestStatus.Passed, 1, null)]);
        var passingEvidence = new FlakinessEvidence("abcdef", 2, 1, 0, ["feature-x"], [new("r9", at, TestStatus.Failed, 0, "feature-x")]);
        var engine = new StubEngine
        {
            LatestFailures =
            [
                new FailureClassification("A.Tests", "A.Wobbles", FailureKind.Flaky, failingEvidence.Describe(), failingEvidence)
                {
                    ProjectId = "One/A.Tests.csproj",
                },
            ],
            Histories =
            [
                new TestHistory("A.Tests", "A.Wobbles", [], failingEvidence) { ProjectId = "One/A.Tests.csproj" },
                new TestHistory("A.Tests", "A.Wobbles", [], passingEvidence) { ProjectId = "Two/A.Tests.csproj" },
            ],
        };

        var result = await new EngineCommandDispatcher(engine).HandleCommandAsync(ProtocolMethods.HistoryGetTestMarks, null, _ct);

        var marks = result!["tests"]!.AsArray().ToDictionary(m => m!["projectId"]!.GetValue<string>());
        Assert.Equal(2, marks.Count);
        var failing = marks["One/A.Tests.csproj"]!["flakiness"]!;
        Assert.Null(failing["source"]);
        Assert.Equal(1, failing["retryOutcomes"]!.GetValue<int>());
        Assert.Equal(1, failing["outcomes"]![1]!["attempt"]!.GetValue<int>());
        Assert.StartsWith("Passed 1 and failed 1 time(s) within run r2", failing["summary"]!.GetValue<string>());
        Assert.Null(marks["Two/A.Tests.csproj"]!["failureKind"]);
        var passing = marks["Two/A.Tests.csproj"]!["flakiness"]!;
        Assert.Equal("abcdef", passing["source"]!.GetValue<string>());
        Assert.Equal(["feature-x"], passing["otherWorktrees"]!.AsArray().Select(w => w!.GetValue<string>()));
        Assert.Equal("feature-x", passing["outcomes"]![0]!["worktree"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetRun_IncludesRetryAttemptsAndCounts()
    {
        var run = Run("r1", new StoredTestResult("A.Tests", "A.Wobbles", TestStatus.Passed, 12, null, null,
            [new StoredAttempt(TestStatus.Failed, 8, "first")])
        { ProjectId = "One/A.Tests.csproj" });
        var engine = new StubEngine { StoredRuns = [run with { Summary = run.Summary with { Retried = 1, PassedOnRetry = 1 } }] };

        var result = await new EngineCommandDispatcher(engine).HandleCommandAsync(ProtocolMethods.HistoryGetRun, new JsonObject { ["runId"] = "r1" }, _ct);

        Assert.Equal(1, result!["summary"]!["retried"]!.GetValue<int>());
        Assert.Equal(1, result["summary"]!["passedOnRetry"]!.GetValue<int>());
        Assert.Equal("One/A.Tests.csproj", result["results"]![0]!["projectId"]!.GetValue<string>());
        var attempt = Assert.Single(result["results"]![0]!["attempts"]!.AsArray())!;
        Assert.Equal("Failed", attempt["status"]!.GetValue<string>());
        Assert.Equal("first", attempt["errorMessage"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetTestMarks_WithHistoryOff_ReportsDisabled()
    {
        var result = await new EngineCommandDispatcher(new StubEngine()).HandleCommandAsync(ProtocolMethods.HistoryGetTestMarks, null, _ct);

        Assert.False(result!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GetTestHistory_ReturnsEntriesOldestFirstWithStatusAsString()
    {
        var engine = new StubEngine
        {
            Histories =
            [
                new TestHistory("A.Tests", "A.One", [
                    new TestHistoryEntry("r1", DateTimeOffset.UnixEpoch, TestStatus.Passed, 5, "abc"),
                    new TestHistoryEntry("r2", DateTimeOffset.UnixEpoch.AddMinutes(1), TestStatus.Failed, 9, null),
                ], Flakiness: null) { ProjectId = "tests/A.Tests/A.Tests.csproj" },
            ],
        };
        var dispatcher = new EngineCommandDispatcher(engine);

        var result = await dispatcher.HandleCommandAsync(
            ProtocolMethods.HistoryGetTestHistory, new JsonObject { ["test"] = "A.One", ["projectId"] = "tests/A.Tests/A.Tests.csproj" }, _ct);

        Assert.True(result!["enabled"]!.GetValue<bool>());
        var test = Assert.Single(result["tests"]!.AsArray())!;
        Assert.Equal(["Passed", "Failed"], test["runs"]!.AsArray().Select(r => r!["status"]!.GetValue<string>()));
        Assert.Equal("tests/A.Tests/A.Tests.csproj", engine.LastHistoryProject);
    }

    [Fact]
    public async Task GetTestHistory_UnknownProject_IsInvalidParams()
    {
        var engine = new StubEngine { GetTestHistoryError = new ArgumentException("'Unknown/Tests.csproj' is not a test project ID in this solution.") };
        var dispatcher = new EngineCommandDispatcher(engine);

        var error = await Assert.ThrowsAsync<JsonRpcException>(() => dispatcher.HandleCommandAsync(
            ProtocolMethods.HistoryGetTestHistory, new JsonObject { ["test"] = "A.One", ["projectId"] = "Unknown/Tests.csproj" }, _ct));

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, error.Code);
        Assert.Equal(engine.GetTestHistoryError.Message, error.Message);
    }

    [Fact]
    public async Task GetTestHistory_WithHistoryOff_ReportsDisabled()
    {
        var result = await new EngineCommandDispatcher(new StubEngine()).HandleCommandAsync(
            ProtocolMethods.HistoryGetTestHistory, new JsonObject { ["test"] = "A.One" }, _ct);

        Assert.False(result!["enabled"]!.GetValue<bool>());
        Assert.Empty(result["tests"]!.AsArray());
    }

    [Fact]
    public async Task GetRun_UnknownOrMissingId_ReturnsNull()
    {
        var dispatcher = new EngineCommandDispatcher(new StubEngine { StoredRuns = [Run("r1")] });

        Assert.Null(await dispatcher.HandleCommandAsync(ProtocolMethods.HistoryGetRun, new JsonObject { ["runId"] = "nope" }, _ct));
        Assert.Null(await dispatcher.HandleCommandAsync(ProtocolMethods.HistoryGetRun, null, _ct));
    }
}
