// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using ModelContextProtocol;
using Piston.Engine;
using Piston.Engine.Models;
using Piston.Mcp.Tools;
using Xunit;

namespace Piston.Mcp.Tests;

public sealed class TestRunToolTests
{
    private static readonly SourceFingerprint Source = new("piston-source-v2", new string('a', 64), 3);

    private static TestRunRecord Run(string id, long sequence, TestRunStatus status = TestRunStatus.Completed,
        TestRunScope scope = TestRunScope.Full, string? filter = null, ResultFreshness freshness = ResultFreshness.Current) => new()
    {
        RunId = id,
        EngineInstanceId = "engine",
        Sequence = sequence,
        Trigger = TestRunTrigger.Forced,
        Status = status,
        Scope = scope,
        Freshness = freshness,
        RequestedAt = DateTimeOffset.UnixEpoch,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        SourceBefore = Source,
        SourceAfter = Source,
        Context = new TestRunContext("/w/App.slnx", ["/w/T/T.csproj"], null, filter, filter, null, false,
            "Debug", "10.0.401", ".NET 10", "Linux", "/daemon",
            new Dictionary<string, string> { ["/w/T/T.csproj"] = "/w" }),
        Projects = [new TestRunProjectOutcome("/w/T/T.csproj", ProjectRunStatus.Completed, 2)],
        Suites =
        [
            new TestSuite("T",
            [
                new TestResult("Ns.B.Second", "Second", TestStatus.Failed, TimeSpan.FromMilliseconds(2), null, "boom", null, null),
                new TestResult("Ns.A.First", "First", TestStatus.Passed, TimeSpan.FromMilliseconds(1), null, null, null, null),
            ], DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(3)) { RunId = id },
        ],
    };

    [Fact]
    public async Task RunTestsReportsTheRequestedRunsProvenance()
    {
        var engine = new StubEngine { NextRun = Run("run-1", 2) };
        var tools = new TestTools(engine, NullMcpCallRecorder.Instance);

        var result = await tools.RunTests(CancellationToken.None);

        Assert.Contains("Run: run-1 | Status: Completed | Scope: Full | Filtered: no | Freshness: Current", result);
        Assert.Contains($"Source: piston-source-v2:{Source.Value}", result);
        Assert.Contains("Run tests: 2 (Passed: 1, Failed: 1, Skipped: 0)", result);
        Assert.Contains("GetTestRun(\"run-1\")", result);
    }

    [Fact]
    public void GetTestRunReturnsRunBoundJsonWithSortedIdentities()
    {
        var engine = new StubEngine();
        var older = Run("run-1", 1);
        var latest = Run("run-2", 2, scope: TestRunScope.Selective);
        engine.State.RecentRuns = [latest, older];
        engine.State.LastRun = latest;
        var tools = new TestTools(engine, NullMcpCallRecorder.Instance);

        using var doc = JsonDocument.Parse(tools.GetTestRun("run-1"));
        var root = doc.RootElement;

        Assert.Equal("piston.test-run/v1", root.GetProperty("schema").GetString());
        Assert.Equal("run-1", root.GetProperty("runId").GetString());
        Assert.Equal("Completed", root.GetProperty("status").GetString());
        Assert.Equal("Full", root.GetProperty("scope").GetString());
        Assert.True(root.GetProperty("isCurrentFullResult").GetBoolean());
        Assert.Equal(Source.Value, root.GetProperty("sourceAfter").GetProperty("value").GetString());
        Assert.Equal("10.0.401", root.GetProperty("context").GetProperty("sdkVersion").GetString());
        Assert.Equal("/daemon", root.GetProperty("context").GetProperty("workingDirectory").GetString());
        Assert.Equal("/w", root.GetProperty("context").GetProperty("testWorkingDirectories")
            .GetProperty("/w/T/T.csproj").GetString());
        var tests = root.GetProperty("tests").EnumerateArray()
            .Select(t => (t.GetProperty("fullyQualifiedName").GetString(), t.GetProperty("status").GetString()))
            .ToList();
        Assert.Equal([("Ns.A.First", "Passed"), ("Ns.B.Second", "Failed")], tests);

        using var latestDoc = JsonDocument.Parse(tools.GetTestRun(""));
        Assert.Equal("run-2", latestDoc.RootElement.GetProperty("runId").GetString());
        Assert.False(latestDoc.RootElement.GetProperty("isCurrentFullResult").GetBoolean());
    }

    [Fact]
    public void GetTestRunReportsFilteredAndStaleRunsAsNotCurrentFullResults()
    {
        var engine = new StubEngine();
        engine.State.RecentRuns =
        [
            Run("filtered", 1, filter: "FullyQualifiedName~A"),
            Run("stale", 2, freshness: ResultFreshness.Stale),
        ];
        var tools = new TestTools(engine, NullMcpCallRecorder.Instance);

        using var filtered = JsonDocument.Parse(tools.GetTestRun("filtered"));
        Assert.True(filtered.RootElement.GetProperty("isFiltered").GetBoolean());
        Assert.False(filtered.RootElement.GetProperty("isFullScope").GetBoolean());
        Assert.False(filtered.RootElement.GetProperty("isCurrentFullResult").GetBoolean());

        using var stale = JsonDocument.Parse(tools.GetTestRun("stale"));
        Assert.Equal("Stale", stale.RootElement.GetProperty("freshness").GetString());
        Assert.False(stale.RootElement.GetProperty("isCurrentFullResult").GetBoolean());
    }

    [Fact]
    public void GetTestRunReturnsActiveRun()
    {
        var engine = new StubEngine();
        engine.State.CurrentRun = Run("active-run", 3, status: TestRunStatus.Running, freshness: ResultFreshness.Unknown);
        var tools = new TestTools(engine, NullMcpCallRecorder.Instance);

        using var doc = JsonDocument.Parse(tools.GetTestRun("active"));

        Assert.Equal("Running", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.GetProperty("isTerminal").GetBoolean());
    }

    [Fact]
    public void GetTestRunRejectsUnknownRun()
    {
        var tools = new TestTools(new StubEngine(), NullMcpCallRecorder.Instance);

        var ex = Assert.Throws<McpException>(() => tools.GetTestRun("missing"));
        Assert.Contains("missing", ex.Message);
        Assert.Throws<McpException>(() => tools.GetTestRun(""));
    }

    [Fact]
    public void GetTestResultsIdentifiesLatestRunAndSuiteRuns()
    {
        var engine = new StubEngine();
        var run = Run("run-9", 9);
        engine.State.LastRun = run;
        engine.State.TestSuites = run.Suites;
        var tools = new TestTools(engine, NullMcpCallRecorder.Instance);

        var result = tools.GetTestResults();

        Assert.Contains("Latest Run: run-9 | Status: Completed", result);
        Assert.Contains("Suite: T (run run-9)", result);
        Assert.Contains("may combine several runs", result);
    }
}
