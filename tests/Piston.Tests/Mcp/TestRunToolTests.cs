// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using ModelContextProtocol;
using Piston.Engine;
using Piston.Engine.Models;
using Piston.Hosting.Mapping;
using Piston.Mcp.Tools;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Xunit;

namespace Piston.Tests.Mcp;

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

        var result = await tools.RunTestsAsync(ct: CancellationToken.None);

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

    [Theory]
    [InlineData(TestRunStatus.Queued)]
    [InlineData(TestRunStatus.Cancelled)]
    public void GetTestRunIncludesSolutionBeforeExecutionContextExists(TestRunStatus status)
    {
        var engine = new StubEngine();
        var run = Run("early", 1, status: status) with
        {
            SolutionPath = "/w/App.slnx",
            Context = null,
            StartedAt = null,
        };
        engine.State.CurrentRun = status == TestRunStatus.Queued ? run : null;
        engine.State.RecentRuns = [run];
        var tools = new TestTools(engine, NullMcpCallRecorder.Instance);

        using var doc = JsonDocument.Parse(tools.GetTestRun(status == TestRunStatus.Queued ? "active" : run.RunId));
        Assert.Equal("/w/App.slnx", doc.RootElement.GetProperty("solutionPath").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("context").ValueKind);
    }

    [Fact]
    public void BuildFailureDiagnosticsAreRunBoundInMcpAndProtocol()
    {
        const string diagnostic = "/w/Code.cs(15,9): error CS0103: MissingSymbol [App.csproj]";
        var failed = Run("failed-build", 1, status: TestRunStatus.BuildFailed) with
        {
            Build = new BuildResult(BuildStatus.Failed, [diagnostic], [], TimeSpan.FromMilliseconds(12)),
            Suites = [],
            Projects = [],
        };
        var engine = new StubEngine();
        engine.State.RecentRuns = [Run("newer", 2), failed];
        engine.State.LastBuild = new BuildResult(BuildStatus.Succeeded, [], [], TimeSpan.Zero);
        var tools = new TestTools(engine, NullMcpCallRecorder.Instance);
        using var doc = JsonDocument.Parse(tools.GetTestRun(failed.RunId));
        var root = doc.RootElement;
        Assert.Equal("BuildFailed", root.GetProperty("status").GetString());
        Assert.Equal("Current", root.GetProperty("freshness").GetString());
        Assert.False(root.GetProperty("isCurrentFullResult").GetBoolean());
        Assert.Equal(0, root.GetProperty("tests").GetArrayLength());
        Assert.Equal("Failed", root.GetProperty("build").GetProperty("status").GetString());
        Assert.Equal(diagnostic, root.GetProperty("build").GetProperty("errors")[0].GetString());

        var json = JsonSerializer.Serialize(failed.ToDto(), JsonRpcSerializer.Options);
        var dto = JsonSerializer.Deserialize<TestRunDto>(json, JsonRpcSerializer.Options)!;
        Assert.Equal(failed.RunId, dto.RunId);
        Assert.Equal(BuildStatusDto.Failed, dto.Build!.Status);
        Assert.Equal([diagnostic], dto.Build.Errors);
        Assert.Equal(12, dto.Build.DurationMs);
        Assert.Null(Run("no-build", 3).ToDto().Build);
    }

    [Fact]
    public void SelectionExplanationAndReasonsRoundTripThroughTheProtocolAndMcp()
    {
        var reason = new TestSelectionReason("/w/src/A.cs", "4", "3-5", "class", "/w/T/T.csproj");
        var baseRun = Run("selective", 4, scope: TestRunScope.Selective);
        var run = baseRun with
        {
            Context = baseRun.Context! with
            {
                SelectionExplanation = "Selected by changed lines.",
                SelectionReasons = new Dictionary<string, IReadOnlyList<TestSelectionReason>> { ["Ns.A.First"] = [reason] },
            },
        };

        var json = JsonSerializer.Serialize(run.ToDto(), JsonRpcSerializer.Options);
        var dto = JsonSerializer.Deserialize<TestRunDto>(json, JsonRpcSerializer.Options)!;

        Assert.Equal("Selected by changed lines.", dto.SelectionExplanation);
        var mapped = Assert.Single(Assert.Single(dto.SelectionReasons!).Value);
        Assert.Equal(("/w/src/A.cs", "4", "3-5", "class"), (mapped.FilePath, mapped.ChangedLines, mapped.ExecutedLines, mapped.Granularity));
        Assert.Null(Run("full", 5).ToDto().SelectionReasons);

        var engine = new StubEngine();
        engine.State.RecentRuns = [run, Run("full", 5)];
        var tools = new TestTools(engine, NullMcpCallRecorder.Instance);
        using var document = JsonDocument.Parse(tools.GetTestRun("selective"));
        var context = document.RootElement.GetProperty("context");
        Assert.Equal("Selected by changed lines.", context.GetProperty("selectionExplanation").GetString());
        var reasons = context.GetProperty("selectionReasons").GetProperty("Ns.A.First");
        Assert.Equal(1, reasons.GetArrayLength());
        Assert.Equal(reason.FilePath, reasons[0].GetProperty("filePath").GetString());
        Assert.Equal(reason.ChangedLines, reasons[0].GetProperty("changedLines").GetString());
        Assert.Equal(reason.ExecutedLines, reasons[0].GetProperty("executedLines").GetString());
        Assert.Equal(reason.Granularity, reasons[0].GetProperty("granularity").GetString());
        Assert.Equal(reason.ProjectPath, reasons[0].GetProperty("projectPath").GetString());

        using var fullDocument = JsonDocument.Parse(tools.GetTestRun("full"));
        var fullContext = fullDocument.RootElement.GetProperty("context");
        Assert.Equal(JsonValueKind.Null, fullContext.GetProperty("selectionExplanation").ValueKind);
        Assert.Equal(JsonValueKind.Null, fullContext.GetProperty("selectionReasons").ValueKind);
    }

    [Fact]
    public void GetTestRunRejectsUnknownRun()
    {
        var tools = new TestTools(new StubEngine(), NullMcpCallRecorder.Instance);

        var ex = Assert.Throws<McpException>(() => tools.GetTestRun("missing"));
        Assert.Contains("missing", ex.Message);
        _ = Assert.Throws<McpException>(() => tools.GetTestRun(""));
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
    private static TestRunAdmission QueuedAdmission() => new(
        TestRunAdmissionState.Queued, 2, 512, 4, 4096, "/home/u/.local/share/Piston/execution/budget.json",
        1, 2, 2, 3, 1024, DateTimeOffset.UnixEpoch, null);

    [Fact]
    public void GetTestRunAndResultsReportQueuedMachineAdmission()
    {
        var engine = new StubEngine();
        engine.State.CurrentRun = Run("queued-run", 4, status: TestRunStatus.Queued, freshness: ResultFreshness.Unknown)
            with
        { StartedAt = null, Admission = QueuedAdmission() };
        var tools = new TestTools(engine, NullMcpCallRecorder.Instance);

        using var doc = JsonDocument.Parse(tools.GetTestRun("active"));
        var admission = doc.RootElement.GetProperty("admission");
        Assert.Equal("Queued", admission.GetProperty("state").GetString());
        Assert.Equal(1, admission.GetProperty("queuedAhead").GetInt32());
        Assert.Equal(2, admission.GetProperty("queueLength").GetInt32());
        Assert.Equal(3, admission.GetProperty("cpuSlotsInUse").GetInt32());
        Assert.Equal(512, admission.GetProperty("reservedMemoryMb").GetInt32());
        Assert.Equal(JsonValueKind.Null, admission.GetProperty("admittedAt").ValueKind);

        var results = tools.GetTestResults();
        Assert.Contains("Active Run: queued-run | Status: Queued", results);
        Assert.Contains("Admission: Queued (1 ahead, 2 waiting); machine budget CPU slots 3+2/4, memory 1024+512/4096 MiB (/home/u/.local/share/Piston/execution/budget.json)", results);
    }

    [Fact]
    public void GetTestRunOmitsAdmissionWhenNoneWasRecorded()
    {
        var engine = new StubEngine();
        engine.State.LastRun = Run("plain", 1);
        var tools = new TestTools(engine, NullMcpCallRecorder.Instance);

        using var doc = JsonDocument.Parse(tools.GetTestRun("latest"));
        Assert.True(!doc.RootElement.TryGetProperty("admission", out var a) || a.ValueKind == JsonValueKind.Null);
    }

    [Theory]
    [InlineData(TestRunAdmissionState.Queued)]
    [InlineData(TestRunAdmissionState.Admitted)]
    public async Task ToolsReportMemoryReservationWhenMemoryIsUnbudgeted(TestRunAdmissionState admissionState)
    {
        var admission = QueuedAdmission() with { State = admissionState, BudgetMemoryMb = null };
        var run = Run("unbudgeted-run", 5) with { Admission = admission };
        var engine = new StubEngine { NextRun = run };
        engine.State.CurrentRun = run;
        engine.State.LastRun = run;
        var tools = new TestTools(engine, NullMcpCallRecorder.Instance);

        var results = tools.GetTestResults();
        Assert.Contains("Active Run: unbudgeted-run", results);
        Assert.Contains("Latest Run: unbudgeted-run", results);
        Assert.Equal(2, results.Split("memory 1024+512 MiB (unbudgeted)", StringSplitOptions.None).Length - 1);
        Assert.Contains("memory 1024+512 MiB (unbudgeted)", await tools.RunTestsAsync(ct: CancellationToken.None));

        using var doc = JsonDocument.Parse(tools.GetTestRun("latest"));
        var recorded = doc.RootElement.GetProperty("admission");
        Assert.Equal(512, recorded.GetProperty("reservedMemoryMb").GetInt32());
        Assert.Equal(JsonValueKind.Null, recorded.GetProperty("budgetMemoryMb").ValueKind);
    }
}
