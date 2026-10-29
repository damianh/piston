// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Piston.Engine;
using Piston.Engine.Models;
using Piston.Mcp.Tools;
using Xunit;

namespace Piston.Tests.Mcp;

public sealed class TestToolsTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static readonly IMcpCallRecorder NullRecorder = NullMcpCallRecorder.Instance;

    [Fact]
    public void RunTestsToolMetadataPreservesWireName()
    {
        var tools = new TestTools(new StubEngine(), NullRecorder);
        var method = typeof(TestTools).GetMethod(nameof(TestTools.RunTestsAsync))!;
        var tool = McpServerTool.Create(method, tools);

        Assert.Equal("run_tests", tool.ProtocolTool.Name);
    }

    [Fact]
    public async Task RecordedCallsUseMcpToolNames()
    {
        var recorder = new CapturingRecorder();
        var engine = new StubEngine();
        var tools = new TestTools(engine, recorder);
        var advertised = typeof(TestTools).GetMethods()
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => McpServerTool.Create(m, tools).ProtocolTool.Name)
            .ToHashSet();

        _ = tools.GetTestResults();
        _ = Assert.ThrowsAny<Exception>(() => tools.GetTestRun("missing"));
        _ = Assert.ThrowsAny<Exception>(() => tools.GetTestResult("missing"));
        _ = tools.SetTestFilter("");
        _ = tools.ClearResults();
        _ = await tools.RunTestsAsync(ct: CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), _ct);

        Assert.Equal(
            ["get_test_results", "get_test_run", "get_test_result", "set_test_filter", "clear_results", "run_tests"],
            recorder.ToolNames);
        Assert.All(recorder.ToolNames, name => Assert.Contains(name, advertised));
    }

    private sealed class CapturingRecorder : IMcpCallRecorder
    {
        public List<string> ToolNames { get; } = [];

        public void Record(string toolName, string? paramsSummary, string? resultSummary, double durationMs, bool succeeded)
            => ToolNames.Add(toolName);
    }

    [Theory]
    [InlineData(PistonPhase.Idle)]
    [InlineData(PistonPhase.Watching)]
    public async Task RunTestsReturnsResultSummary(PistonPhase completedPhase)
    {
        var engine = new StubEngine { CompletedPhase = completedPhase };
        engine.State.Phase = PistonPhase.Idle;
        engine.State.CompletedTests = 5;
        engine.State.TotalExpectedTests = 5;
        engine.State.TestSuites =
        [
            new TestSuite("Suite1",
            [
                new TestResult("Test1", "Test1", TestStatus.Passed, TimeSpan.FromMilliseconds(100), null, null, null, null),
                new TestResult("Test2", "Test2", TestStatus.Passed, TimeSpan.FromMilliseconds(200), null, null, null, null),
            ], DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(300)),
        ];

        var tools = new TestTools(engine, NullRecorder);
        var result = await tools.RunTestsAsync(ct: CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), _ct);

        Assert.True(engine.ForceRunCalled);
        Assert.Contains($"Phase: {completedPhase}", result);
        Assert.Contains("Passed: 2", result);
        Assert.Contains("Suites: 1", result);
    }

    [Fact]
    public async Task RunTestsCancellationStopsWaitingForEngine()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new StubEngine { ForceRunCompletion = completion.Task };
        var tools = new TestTools(engine, NullRecorder);
        using var cts = new CancellationTokenSource();
        var run = tools.RunTestsAsync(ct: cts.Token);
        cts.Cancel();

        try
        {
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(2), _ct));
            Assert.True(engine.ForceRunCalled);
        }
        finally
        {
            completion.SetResult();
        }
    }

    [Fact]
    public void GetTestHistoryListsRunsNewestFirstAsRecorded()
    {
        var at = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var engine = new StubEngine
        {
            Histories =
            [
                new("A.Tests", "A.Tests.One [net10.0]",
                [
                    new("run-1", at, TestStatus.Passed, 12.4, "abcdef0123456789"),
                    new("run-2", at.AddMinutes(1), TestStatus.Failed, 3, null),
                ], Flakiness: null) { ProjectId = "tests/A.Tests/A.Tests.csproj" },
            ],
        };

        var text = new TestTools(engine, NullRecorder).GetTestHistory("A.Tests.One", " tests/A.Tests/A.Tests.csproj ");

        Assert.Equal("tests/A.Tests/A.Tests.csproj", engine.LastHistoryProject);
        Assert.StartsWith("A.Tests [tests/A.Tests/A.Tests.csproj] :: A.Tests.One [net10.0]", text, StringComparison.Ordinal);
        var passed = text.IndexOf("2025-03-04 05:06:07Z Passed        12ms run run-1 (abcdef012345)", StringComparison.Ordinal);
        var failed = text.IndexOf("2025-03-04 05:07:07Z Failed         3ms run run-2 (unknown source)", StringComparison.Ordinal);
        Assert.True(failed > 0 && passed > failed, text);
    }

    [Fact]
    public void GetTestHistoryWithoutATestListsFlakyTests()
    {
        var engine = new StubEngine
        {
            Histories =
            [
                new("A.Tests", "A.Tests.Steady", [], Flakiness: null),
                new("A.Tests", "A.Tests.Wobbly", [], Flakiness: new FlakinessEvidence("abc", 1, 1, 0, [], [])) { ProjectId = "tests/A.Tests/A.Tests.csproj" },
            ],
        };

        var text = new TestTools(engine, NullRecorder).GetTestHistory();

        Assert.Equal("A.Tests [tests/A.Tests/A.Tests.csproj] :: A.Tests.Wobbly (flaky)\n  Evidence: Passed 1 and failed 1 time(s) on source abc.", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void GetTestHistoryShowsEvidenceRetriesAndOtherWorktreeOutcomes()
    {
        var at = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var source = "abcdef0123456789";
        var evidence = new FlakinessEvidence(source, 2, 1, 1, ["feature-x"],
        [
            new("run-2", at.AddMinutes(1), TestStatus.Passed, 1, null),
            new("run-2", at.AddMinutes(1), TestStatus.Failed, 0, null),
            new("run-9", at, TestStatus.Passed, 0, "feature-x"),
        ]);
        var engine = new StubEngine
        {
            Histories =
            [
                new("A.Tests", "A.Tests.One",
                [
                    new("run-2", at.AddMinutes(1), TestStatus.Failed, 3, source),
                    new("run-2", at.AddMinutes(1), TestStatus.Passed, 4, source, Attempt: 1),
                ], evidence) { ProjectId = "tests/A.Tests/A.Tests.csproj" },
            ],
        };

        var text = new TestTools(engine, NullRecorder).GetTestHistory("A.Tests.One");

        Assert.Contains("A.Tests [tests/A.Tests/A.Tests.csproj] :: A.Tests.One (flaky)", text);
        Assert.Contains("  Evidence: Passed 2 and failed 1 time(s) on source abcdef012345 (1 from in-run retries; other worktrees: feature-x).", text);
        Assert.Contains("  2025-03-04 05:06:07Z Passed  run run-9 in worktree feature-x", text);
        Assert.Contains("  2025-03-04 05:07:07Z Passed         4ms run run-2 retry 1 (abcdef012345)", text);
        Assert.Contains("  2025-03-04 05:07:07Z Failed         3ms run run-2 (abcdef012345)", text);
    }

    [Fact]
    public void GetTestHistoryExplainsWhenHistoryIsOffOrEmpty()
    {
        Assert.Contains("history is off", new TestTools(new StubEngine(), NullRecorder).GetTestHistory("A.B"), StringComparison.OrdinalIgnoreCase);
        var engine = new StubEngine { Histories = [] };
        Assert.Equal("No history is recorded for 'A.B'.", new TestTools(engine, NullRecorder).GetTestHistory("A.B"));
        Assert.Equal("No flaky tests are recorded.", new TestTools(engine, NullRecorder).GetTestHistory(""));
    }

    [Fact]
    public void GetTestResultsReturnsFormattedOutput()
    {
        var engine = new StubEngine();
        engine.State.Phase = PistonPhase.Idle;
        engine.State.CompletedTests = 3;
        engine.State.TotalExpectedTests = 3;
        engine.State.TestSuites =
        [
            new TestSuite("Suite1",
            [
                new TestResult("NS.Test1", "Test1", TestStatus.Passed, TimeSpan.FromMilliseconds(50), null, null, null, null),
                new TestResult("NS.Test2", "Test2", TestStatus.Failed, TimeSpan.FromMilliseconds(100), null, "Assert failed", "at line 5", null),
                new TestResult("NS.Test3", "Test3", TestStatus.Skipped, TimeSpan.Zero, null, null, null, null),
            ], DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(150)),
        ];

        var tools = new TestTools(engine, NullRecorder);
        var result = tools.GetTestResults();

        Assert.Contains("Phase: Idle", result);
        Assert.Contains("Passed: 1", result);
        Assert.Contains("Failed: 1", result);
        Assert.Contains("Skipped: 1", result);
        Assert.Contains("Suite: Suite1", result);
        Assert.Contains("[Passed] Test1", result);
        Assert.Contains("[Failed] Test2", result);
        Assert.Contains("Assert failed", result);
        Assert.Contains("[Skipped] Test3", result);
        Assert.Contains("50ms", result);
    }

    [Fact]
    public void GetTestResultsWithNoSuitesReturnsEmpty()
    {
        var engine = new StubEngine();
        engine.State.Phase = PistonPhase.Idle;

        var tools = new TestTools(engine, NullRecorder);
        var result = tools.GetTestResults();

        Assert.Contains("Phase: Idle", result);
        Assert.Contains("Tests: 0/0", result);
    }

    [Fact]
    public void SetTestFilterSetsFilter()
    {
        var engine = new StubEngine();

        var tools = new TestTools(engine, NullRecorder);
        var result = tools.SetTestFilter("MyTest");

        Assert.Equal("MyTest", engine.LastFilter);
        Assert.Contains("MyTest", result);
    }

    [Fact]
    public void SetTestFilterEmptyClearsFilter()
    {
        var engine = new StubEngine();

        var tools = new TestTools(engine, NullRecorder);
        var result = tools.SetTestFilter("");

        Assert.Null(engine.LastFilter);
        Assert.Contains("(none)", result);
    }

    [Fact]
    public void ClearResultsCallsEngine()
    {
        var engine = new StubEngine();

        var tools = new TestTools(engine, NullRecorder);
        var result = tools.ClearResults();

        Assert.True(engine.ClearResultsCalled);
        Assert.Contains("Results cleared", result);
        var description = typeof(TestTools).GetMethod(nameof(TestTools.ClearResults))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;
        Assert.Contains("persisted coverage in the current solution directory", description);
    }
}
