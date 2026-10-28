// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.History;
using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

/// <summary>Opt-in retries of a run's failed tests, which gather flakiness evidence.</summary>
public sealed class OrchestratorRetryTests : IDisposable
{
    private static readonly string[] Projects = ["/src/A.Tests/A.Tests.csproj", "/src/B.Tests/B.Tests.csproj"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-retries-{Guid.NewGuid():N}");
    private readonly PistonState _state = new();
    private readonly ProvenanceTestRunner _runner = new();
    private readonly PistonOrchestrator _orchestrator;

    public OrchestratorRetryTests()
    {
        _ = Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "App.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Code { }");
        _orchestrator = new PistonOrchestrator(
            new StubFileWatcherService(), new ProvenanceBuildService(), _runner, new ProvenanceImpactAnalyzer(Projects), _state);
    }

    private string SolutionPath => Path.Combine(_root, "App.slnx");

    public void Dispose()
    {
        _orchestrator.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_root + "-history", recursive: true); } catch { /* best effort */ }
    }

    private async Task<TestRunRecord> RunAsync()
    {
        _runner.Calls.Clear();
        return (await _orchestrator.RequestRunAsync())!;
    }

    private async Task StartAsync()
    {
        await _orchestrator.StartAsync(SolutionPath);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_state.LastRun is not { Trigger: TestRunTrigger.Startup })
        {
            Assert.True(DateTime.UtcNow < deadline, "The startup run did not finish.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static TestResult Test(TestRunRecord run, string name) =>
        Assert.Single(run.Suites.SelectMany(s => s.Tests), t => t.FullyQualifiedName == name);

    [Fact]
    public async Task Failed_tests_are_not_retried_by_default()
    {
        await StartAsync();
        _runner.FailingTest = "B.Tests.Test1";

        var run = await RunAsync();

        _ = Assert.Single(_runner.Calls);
        Assert.Equal(0, run.RetryLimit);
        Assert.Empty(Test(run, "B.Tests.Test1").Attempts);
    }

    [Fact]
    public async Task A_test_that_passes_on_retry_counts_as_passed_and_keeps_its_failed_attempt()
    {
        _orchestrator.RetryFailedTests = 2;
        await StartAsync();
        _runner.FailingTest = "B.Tests.Test1";
        _runner.FailuresRemaining = 1;

        var run = await RunAsync();

        var test = Test(run, "B.Tests.Test1");
        Assert.Equal(TestStatus.Passed, test.Status);
        var attempt = Assert.Single(test.Attempts);
        Assert.Equal((TestStatus.Failed, "boom"), (attempt.Status, attempt.ErrorMessage));
        Assert.Equal(2, run.RetryLimit);
        Assert.Null(run.RetriesSkippedReason);
        Assert.Equal(2, _runner.Calls.Count);
        Assert.Equal("FullyQualifiedName=B.Tests.Test1", _runner.Calls[1].Filter);
        Assert.False(_runner.Calls[1].Coverage);
        Assert.Null(_state.RetryStatus);
        Assert.Contains(_state.TestSuites.SelectMany(s => s.Tests), t => t.FullyQualifiedName == "B.Tests.Test1" && t.Status == TestStatus.Passed);
    }

    [Fact]
    public async Task A_retry_that_reports_no_framework_does_not_clear_a_framework_specific_failure()
    {
        _orchestrator.RetryFailedTests = 2;
        await StartAsync();
        _runner.FailingTest = "B.Tests.Test1";
        _runner.FailingTestFramework = "net10.0";
        _runner.FailuresRemaining = 1;

        var run = await RunAsync();

        var test = Test(run, "B.Tests.Test1");
        Assert.Equal((TestStatus.Failed, "B.Tests.Test1 [net10.0]"), (test.Status, test.ResultKey));
        Assert.Empty(test.Attempts);
        Assert.Equal(2, _runner.Calls.Count);
        Assert.Contains("unambiguously", run.RetriesSkippedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retry_that_reports_the_test_twice_does_not_clear_the_failure()
    {
        _orchestrator.RetryFailedTests = 1;
        await StartAsync();
        _runner.FailingTest = "B.Tests.Test1";
        _runner.FailuresRemaining = 1;
        _runner.DuringRun = () => _runner.DuplicateFirstTest = _runner.LastFilter is not null;

        var run = await RunAsync();

        var test = Test(run, "B.Tests.Test1");
        Assert.Equal(TestStatus.Failed, test.Status);
        Assert.Empty(test.Attempts);
        Assert.Contains("unambiguously", run.RetriesSkippedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retry_that_fails_a_test_not_being_retried_is_not_used()
    {
        _orchestrator.RetryFailedTests = 2;
        await StartAsync();
        _runner.FailingTest = "B.Tests.Test1";
        _runner.FailuresRemaining = 1;
        _runner.DuringRun = () => _runner.ExtraFailures = _runner.LastFilter is null ? 0 : 1;

        var run = await RunAsync();

        var test = Test(run, "B.Tests.Test1");
        Assert.Equal(TestStatus.Failed, test.Status);
        Assert.Empty(test.Attempts);
        Assert.Equal(2, _runner.Calls.Count);
        Assert.Contains("not being retried", run.RetriesSkippedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retry_that_crashes_is_not_used_and_says_why()
    {
        _orchestrator.RetryFailedTests = 2;
        await StartAsync();
        _runner.FailingTest = "B.Tests.Test1";
        _runner.FailuresRemaining = 1;
        _runner.DuringRun = () =>
        {
            if (_runner.LastFilter is not null)
            {
                _ = _runner.CrashedProjects.Add(Projects[1]);
            }
        };

        var run = await RunAsync();

        var test = Test(run, "B.Tests.Test1");
        Assert.Equal(TestStatus.Failed, test.Status);
        Assert.Empty(test.Attempts);
        Assert.Equal(2, _runner.Calls.Count);
        Assert.Contains("crashed", run.RetriesSkippedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timed_out_project_is_reported_as_not_retried_while_other_failures_are_retried()
    {
        _orchestrator.RetryFailedTests = 1;
        await StartAsync();
        _runner.FailingTest = "B.Tests.Test1";
        _runner.FailuresRemaining = 1;
        _runner.TimedOutProjects[Projects[0]] = new TestTimeout("A.Tests.Slow", TimeSpan.FromSeconds(30));

        var run = await RunAsync();

        Assert.Equal(TestStatus.Passed, Test(run, "B.Tests.Test1").Status);
        Assert.Contains("timed out", run.RetriesSkippedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_that_keeps_failing_is_retried_up_to_the_limit()
    {
        _orchestrator.RetryFailedTests = 2;
        await StartAsync();
        _runner.FailingTest = "B.Tests.Test1";

        var run = await RunAsync();

        var test = Test(run, "B.Tests.Test1");
        Assert.Equal(TestStatus.Failed, test.Status);
        Assert.Equal([TestStatus.Failed, TestStatus.Failed], test.Attempts.Select(a => a.Status));
        Assert.Equal(3, _runner.Calls.Count);
        Assert.Empty(Test(run, "A.Tests.Test1").Attempts);
    }

    [Fact]
    public async Task A_run_with_too_many_failures_is_not_retried_and_says_why()
    {
        _orchestrator.RetryFailedTests = 1;
        await StartAsync();
        _runner.ExtraFailures = PistonOrchestrator.MaxRetriedFailures / 2 + 1;

        var run = await RunAsync();

        _ = Assert.Single(_runner.Calls);
        Assert.Contains("not retried", run.RetriesSkippedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passing_on_retry_is_classified_flaky_with_evidence()
    {
        using var history = RunHistoryStore.Open(_root + "-history", SolutionPath);
        _orchestrator.RunHistory = history;
        _orchestrator.RetryFailedTests = 1;
        await StartAsync();
        _runner.FailingTest = "B.Tests.Test1";
        _runner.FailuresRemaining = 1;

        var run = await RunAsync();

        var classification = Assert.Single(run.FailureClassifications);
        Assert.Equal(FailureKind.Flaky, classification.Kind);
        var evidence = Assert.IsType<FlakinessEvidence>(classification.Flakiness);
        // The startup run passed on the same source too.
        Assert.NotNull(evidence.Source);
        Assert.Equal((2, 1, 1), (evidence.Passed, evidence.Failed, evidence.RetryOutcomes));
        Assert.Equal(1, history.GetRuns()[0].PassedOnRetry);
        _orchestrator.RunHistory = null;
    }
}
