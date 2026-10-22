// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.History;
using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.History;

public sealed class RunHistoryStoreTests : IDisposable
{
    private const string Project = "App.Tests";
    private const string Test = "App.Tests.CalculatorTests.Adds";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "piston-history-tests", Guid.NewGuid().ToString("N"));
    private readonly string _solution;
    private readonly ManualTime _time = new(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private int _sequence;

    public RunHistoryStoreTests()
    {
        var worktree = Path.Combine(_root, "repo");
        _ = Directory.CreateDirectory(worktree);
        _solution = Path.Combine(worktree, "App.slnx");
        File.WriteAllText(_solution, "<Solution />");
    }

    private string BaseDirectory => Path.Combine(_root, "history");

    [Fact]
    public void First_failure_has_unknown_history()
    {
        using var store = Open();

        var classifications = store.Record(Run("s1", TestStatus.Failed));

        Assert.Equal(FailureKind.Unknown, Assert.Single(classifications).Kind);
    }

    [Fact]
    public void Failure_after_a_pass_is_new()
    {
        using var store = Open();
        _ = store.Record(Run("s1", TestStatus.Passed));

        var classification = Assert.Single(store.Record(Run("s2", TestStatus.Failed)));

        Assert.Equal(FailureKind.New, classification.Kind);
        Assert.Equal(Project, classification.Project);
        Assert.Equal(Test, classification.Test);
    }

    [Fact]
    public void Failure_after_a_failure_is_pre_existing()
    {
        using var store = Open();
        _ = store.Record(Run("s1", TestStatus.Passed));
        _ = store.Record(Run("s2", TestStatus.Failed));

        var classification = Assert.Single(store.Record(Run("s3", TestStatus.Failed)));

        Assert.Equal(FailureKind.PreExisting, classification.Kind);
        Assert.Contains("2", classification.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Pass_and_fail_on_the_same_source_is_flaky()
    {
        using var store = Open();
        _ = store.Record(Run("same", TestStatus.Passed));

        Assert.Equal(FailureKind.Flaky, Assert.Single(store.Record(Run("same", TestStatus.Failed))).Kind);
        Assert.Equal(Test, Assert.Single(store.GetFlakyTests()).Test);
    }

    [Fact]
    public void Edit_break_fix_on_different_sources_is_not_flaky()
    {
        using var store = Open();
        _ = store.Record(Run("s1", TestStatus.Passed));
        _ = store.Record(Run("s2", TestStatus.Failed));
        _ = store.Record(Run("s3", TestStatus.Passed));

        Assert.Equal(FailureKind.New, Assert.Single(store.Record(Run("s4", TestStatus.Failed))).Kind);
        Assert.Empty(store.GetFlakyTests());
    }

    [Fact]
    public void Runs_with_unknown_freshness_carry_no_source_and_cannot_be_flaky()
    {
        using var store = Open();
        _ = store.Record(Run("same", TestStatus.Passed) with { Freshness = ResultFreshness.Stale });

        Assert.Equal(FailureKind.New, Assert.Single(store.Record(Run("same", TestStatus.Failed) with { Freshness = ResultFreshness.Stale })).Kind);
        Assert.All(Assert.Single(store.GetTestHistory(Test)).Runs, e => Assert.Null(e.Source));
    }

    [Fact]
    public void Recording_a_run_twice_is_a_no_op()
    {
        using var store = Open();
        var run = Run("s1", TestStatus.Failed);
        _ = store.Record(run);

        Assert.Empty(store.Record(run));
        _ = Assert.Single(Assert.Single(store.GetTestHistory(Test)).Runs);
        _ = Assert.Single(store.GetRuns());
    }

    [Fact]
    public void History_matches_the_name_with_or_without_target_framework()
    {
        using var store = Open();
        _ = store.Record(Run("s1", TestStatus.Passed, targetFramework: "net10.0"));

        Assert.Equal(Test + " [net10.0]", Assert.Single(store.GetTestHistory(Test)).Test);
        Assert.Empty(store.GetTestHistory(Test, "other"));
    }

    [Fact]
    public void Entries_per_test_and_stored_runs_are_capped()
    {
        using var store = Open();
        for (var i = 0; i < RunHistoryStore.MaxStoredRuns + 5; i++)
        {
            _ = store.Record(Run($"s{i}", TestStatus.Passed));
        }

        Assert.Equal(RunHistoryStore.MaxEntriesPerTest, Assert.Single(store.GetTestHistory(Test)).Runs.Count);
        var runs = store.GetRuns();
        Assert.Equal(RunHistoryStore.MaxStoredRuns, runs.Count);
        Assert.Equal($"run-{_sequence}", runs[0].RunId);
    }

    [Fact]
    public void History_survives_save_and_reopen()
    {
        string failedRunId;
        using (var store = Open())
        {
            _ = store.Record(Run("s1", TestStatus.Passed));
            var failed = Run("s2", TestStatus.Failed);
            failedRunId = failed.RunId;
            _ = store.Record(failed);
        }

        using var reopened = Open();
        var history = Assert.Single(reopened.GetTestHistory(Test));
        Assert.Equal([TestStatus.Passed, TestStatus.Failed], history.Runs.Select(e => e.Status));
        Assert.Equal(["s1", "s2"], history.Runs.Select(e => e.Source));
        var run = Assert.IsType<StoredRun>(reopened.GetRun(failedRunId));
        Assert.Equal(1, run.Summary.Failed);
        var result = Assert.Single(run.Results);
        Assert.Equal("boom", result.ErrorMessage);
        Assert.Equal("at Adds()", result.StackTrace);
        Assert.Equal(FailureKind.PreExisting, Assert.Single(reopened.Record(Run("s3", TestStatus.Failed))).Kind);
    }

    [Fact]
    public void A_second_store_for_the_same_worktree_is_read_only()
    {
        using var owner = Open();
        using var second = Open();

        Assert.False(owner.ReadOnly);
        Assert.True(second.ReadOnly);
        _ = second.Record(Run("s1", TestStatus.Passed));
        second.Save();
        Assert.False(File.Exists(second.Location.FilePath));
    }

    [Fact]
    public void Tests_not_run_within_the_retention_period_are_pruned_on_save()
    {
        using (var store = Open())
        {
            _ = store.Record(Run("s1", TestStatus.Passed));
            _time.Now += RunHistoryStore.Retention + TimeSpan.FromDays(1);
        }

        using var reopened = Open();
        Assert.Empty(reopened.GetTestHistory(Test));
    }

    [Fact]
    public void History_inside_the_worktree_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => RunHistoryStore.Open(Path.Combine(Path.GetDirectoryName(_solution)!, ".history"), _solution));

    [Fact]
    public void A_corrupt_file_starts_empty()
    {
        var location = HistoryLocation.Resolve(BaseDirectory, _solution);
        _ = Directory.CreateDirectory(location.Directory);
        File.WriteAllText(location.FilePath, "{ not json");
        var messages = new List<string>();

        using var store = RunHistoryStore.Open(BaseDirectory, _solution, messages.Add, _time);

        Assert.Empty(store.GetRuns());
        Assert.Contains(messages, m => m.Contains(location.FilePath, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private RunHistoryStore Open()
    {
        var store = RunHistoryStore.Open(BaseDirectory, _solution, time: _time);
        store.SaveDelay = TimeSpan.FromMinutes(10);
        return store;
    }

    private TestRunRecord Run(string source, TestStatus status, string? targetFramework = null)
    {
        _sequence++;
        _time.Now += TimeSpan.FromMinutes(1);
        var result = new TestResult(Test, "Adds", status, TimeSpan.FromMilliseconds(12.5), null,
            status == TestStatus.Failed ? "boom" : null, status == TestStatus.Failed ? "at Adds()" : null, null)
        {
            TargetFramework = targetFramework,
        };
        return new TestRunRecord
        {
            RunId = $"run-{_sequence}",
            EngineInstanceId = "engine",
            Sequence = _sequence,
            Trigger = TestRunTrigger.Forced,
            Status = TestRunStatus.Completed,
            Freshness = ResultFreshness.Current,
            RequestedAt = _time.Now,
            CompletedAt = _time.Now,
            SourceAfter = new SourceFingerprint("sha256", source, 1),
            Suites = [new TestSuite(Project, [result], _time.Now, result.Duration)],
        };
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
