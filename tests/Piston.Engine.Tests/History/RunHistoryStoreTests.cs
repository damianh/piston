// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.History;
using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.History;

public sealed class RunHistoryStoreTests : IDisposable
{
    private const string Project = "App.Tests";
    private const string ProjectId = "App.Tests/App.Tests.csproj";
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
    public void Latest_failures_list_tests_whose_last_result_failed_classified_as_when_recorded()
    {
        using var store = Open();
        _ = store.Record(Run("s1", TestStatus.Passed));
        _ = store.Record(Run("s2", TestStatus.Failed));
        _ = store.Record(Run("s2", TestStatus.Skipped));

        var latest = Assert.Single(store.GetLatestFailures());
        Assert.Equal(FailureKind.New, latest.Kind);
        Assert.Equal(Test, latest.Test);

        _ = store.Record(Run("s3", TestStatus.Passed));
        Assert.Empty(store.GetLatestFailures());
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
    public void HistoryOfRemovedProjects_RemainsQueryableByItsStoredId()
    {
        using (var store = Open())
        {
            _ = store.Record(Run("s1", TestStatus.Passed));
        }

        using var reopened = Open();
        reopened.ResolveLegacyProjects(
            [Path.Combine(Path.GetDirectoryName(_solution)!, "Renamed", "Tests.csproj")],
            Path.GetDirectoryName(_solution)!);

        Assert.Equal(ProjectId, Assert.Single(reopened.GetTestHistory(Test, ProjectId)).ProjectId);
        Assert.Empty(reopened.GetTestHistory(Test, "Renamed/Tests.csproj"));
        _ = Assert.Throws<ArgumentException>(() => reopened.GetTestHistory(Test, "Unknown/Tests.csproj"));
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
    public void VersionOneHistory_MigratesUniqueProjectNamesToIds()
    {
        var location = HistoryLocation.Resolve(BaseDirectory, _solution);
        _ = Directory.CreateDirectory(location.Directory);
        File.WriteAllText(location.FilePath, LegacyFile());

        using (var store = Open())
        {
            Assert.Equal(Project, Assert.Single(Assert.IsType<StoredRun>(store.GetRun("legacy-run")).Results).Project);
            store.ResolveLegacyProjects([Path.Combine(Path.GetDirectoryName(_solution)!, "App.Tests", "App.Tests.csproj")],
                Path.GetDirectoryName(_solution)!);
            store.Save();

            var history = Assert.Single(store.GetTestHistory(Test, ProjectId));
            Assert.Equal(ProjectId, history.ProjectId);
            AssertLegacyRun(store);
        }

        using var reopened = Open();
        AssertLegacyRun(reopened);
        Assert.Contains("\"Version\":2", File.ReadAllText(location.FilePath), StringComparison.Ordinal);

        static void AssertLegacyRun(RunHistoryStore store)
        {
            var run = Assert.IsType<StoredRun>(store.GetRun("legacy-run"));
            var result = Assert.Single(run.Results);
            Assert.Equal(ProjectId, result.ProjectId);
            Assert.Equal(Project, result.Project);
            Assert.Equal(Test, result.Test);
            Assert.Equal(TestStatus.Passed, result.Status);
            Assert.Equal(12.5, result.DurationMs);
        }
    }

    [Fact]
    public void VersionOneHistory_DropsProjectNamesThatAreNowAmbiguous()
    {
        var location = HistoryLocation.Resolve(BaseDirectory, _solution);
        _ = Directory.CreateDirectory(location.Directory);
        File.WriteAllText(location.FilePath, LegacyFile());

        using var store = Open();
        store.ResolveLegacyProjects(
            [
                Path.Combine(Path.GetDirectoryName(_solution)!, "App.Tests", "App.Tests.csproj"),
                Path.Combine(Path.GetDirectoryName(_solution)!, "Other", "App.Tests.csproj"),
            ],
            Path.GetDirectoryName(_solution)!);

        Assert.Empty(store.GetTestHistory(Test));
        Assert.Empty(Assert.IsType<StoredRun>(store.GetRun("legacy-run")).Results);
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

    [Fact]
    public void Passing_on_retry_is_flaky_from_in_run_evidence_even_without_a_source()
    {
        using var store = Open();
        var run = Run("s1", TestStatus.Passed, attempts: [TestStatus.Failed], freshness: ResultFreshness.Stale);

        var classification = Assert.Single(store.Record(run));

        Assert.Equal(FailureKind.Flaky, classification.Kind);
        var evidence = Assert.IsType<FlakinessEvidence>(classification.Flakiness);
        Assert.Null(evidence.Source);
        Assert.Equal((1, 1, 1), (evidence.Passed, evidence.Failed, evidence.RetryOutcomes));
        Assert.Contains("retries of the same build", classification.Evidence, StringComparison.Ordinal);
        var history = Assert.Single(store.GetTestHistory(Test));
        Assert.Equal([(TestStatus.Failed, 0), (TestStatus.Passed, 1)], history.Runs.Select(e => (e.Status, e.Attempt)));
        Assert.True(history.Flaky);
        var summary = Assert.Single(store.GetRuns());
        Assert.Equal((1, 1, 0), (summary.Retried, summary.PassedOnRetry, summary.Failed));
    }

    [Fact]
    public void Retry_attempts_survive_save_and_reopen()
    {
        var run = Run("s1", TestStatus.Passed, attempts: [TestStatus.Failed, TestStatus.Failed]);
        using (var store = Open())
        {
            _ = store.Record(run);
        }

        using var reopened = Open();
        var history = Assert.Single(reopened.GetTestHistory(Test));
        Assert.Equal(ProjectId, history.ProjectId);
        Assert.Equal([0, 1, 2], history.Runs.Select(e => e.Attempt));
        var result = Assert.Single(Assert.IsType<StoredRun>(reopened.GetRun(run.RunId)).Results);
        Assert.Equal(ProjectId, result.ProjectId);
        Assert.Equal(TestStatus.Passed, result.Status);
        var attempts = Assert.IsType<IReadOnlyList<StoredAttempt>>(result.Attempts, exactMatch: false);
        Assert.Equal([TestStatus.Failed, TestStatus.Failed], attempts.Select(a => a.Status));
        Assert.Equal("boom", attempts[0].ErrorMessage);
        Assert.Equal(1, reopened.GetRuns()[0].PassedOnRetry);
    }

    [Fact]
    public void Earlier_runs_are_judged_by_their_final_result()
    {
        using var store = Open();
        _ = store.Record(Run("s1", TestStatus.Passed));
        Assert.Equal(FailureKind.New, Assert.Single(store.Record(Run("s2", TestStatus.Failed, attempts: [TestStatus.Failed]))).Kind);

        var classification = Assert.Single(store.Record(Run("s3", TestStatus.Failed)));

        Assert.Equal(FailureKind.PreExisting, classification.Kind);
        Assert.Contains("1 earlier run(s)", classification.Evidence, StringComparison.Ordinal);
        Assert.Equal(FailureKind.PreExisting, Assert.Single(store.GetLatestFailures()).Kind);
    }

    [Fact]
    public void Flaky_evidence_counts_the_outcomes_on_the_source()
    {
        using var store = Open();
        _ = store.Record(Run("same", TestStatus.Passed));
        _ = store.Record(Run("same", TestStatus.Passed));
        _ = store.Record(Run("other", TestStatus.Failed));

        var classification = Assert.Single(store.Record(Run("same", TestStatus.Failed)));

        var evidence = Assert.IsType<FlakinessEvidence>(classification.Flakiness);
        Assert.Equal("same", evidence.Source);
        Assert.Equal((2, 1, 0), (evidence.Passed, evidence.Failed, evidence.RetryOutcomes));
        Assert.Empty(evidence.OtherWorktrees);
        Assert.Equal(3, evidence.Outcomes.Count);
        Assert.Equal(TestStatus.Failed, evidence.Outcomes[0].Status);
        Assert.Contains("Passed 2 and failed 1 time(s)", classification.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Outcomes_in_another_worktree_on_the_same_source_count_as_flaky_evidence()
    {
        var other = CreateLinkedWorktree("feature");
        using (var sibling = RunHistoryStore.Open(BaseDirectory, other, time: _time))
        {
            _ = sibling.Record(Run("same", TestStatus.Passed));
        }

        using var store = Open();
        var classification = Assert.Single(store.Record(Run("same", TestStatus.Failed)));

        Assert.Equal(FailureKind.Flaky, classification.Kind);
        var evidence = Assert.IsType<FlakinessEvidence>(classification.Flakiness);
        Assert.Equal(["feature"], evidence.OtherWorktrees);
        Assert.Equal((1, 1), (evidence.Passed, evidence.Failed));
        Assert.Contains(evidence.Outcomes, o => o.Worktree == "feature" && o.Status == TestStatus.Passed);
        Assert.Contains("other worktrees: feature", classification.Evidence, StringComparison.Ordinal);
        Assert.True(Assert.Single(store.GetTestHistory(Test)).Flaky);
        _ = Assert.Single(store.GetFlakyTests());
    }

    [Fact]
    public void Outcomes_in_another_worktree_older_than_the_retention_are_not_flaky_evidence()
    {
        var other = CreateLinkedWorktree("feature");
        using (var sibling = RunHistoryStore.Open(BaseDirectory, other, time: _time))
        {
            _ = sibling.Record(Run("same", TestStatus.Passed));
        }
        _time.Now += RunHistoryStore.Retention + TimeSpan.FromDays(1);

        using var store = Open();

        Assert.Equal(FailureKind.Unknown, Assert.Single(store.Record(Run("same", TestStatus.Failed))).Kind);
    }

    [Fact]
    public void Another_project_with_the_same_name_in_another_worktree_is_not_flaky_evidence()
    {
        var other = CreateLinkedWorktree("feature");
        using (var sibling = RunHistoryStore.Open(BaseDirectory, other, time: _time))
        {
            var run = Run("same", TestStatus.Passed);
            _ = sibling.Record(run with
            {
                Suites = [Assert.Single(run.Suites) with { ProjectId = "Other/App.Tests.csproj" }],
            });
        }

        using var store = Open();
        var classification = Assert.Single(store.Record(Run("same", TestStatus.Failed)));

        Assert.Equal(ProjectId, classification.ProjectId);
        Assert.Equal(FailureKind.Unknown, classification.Kind);
        Assert.Null(classification.Flakiness);
        Assert.Empty(store.GetFlakyTests());
    }

    [Fact]
    public void Outcomes_of_a_deleted_history_of_another_worktree_stop_counting()
    {
        var other = CreateLinkedWorktree("feature");
        using (var sibling = RunHistoryStore.Open(BaseDirectory, other, time: _time))
        {
            _ = sibling.Record(Run("same", TestStatus.Passed));
        }

        using var store = Open();
        Assert.Equal(FailureKind.Flaky, Assert.Single(store.Record(Run("same", TestStatus.Failed))).Kind);

        File.Delete(HistoryLocation.Resolve(BaseDirectory, other).FilePath);

        Assert.NotEqual(FailureKind.Flaky, Assert.Single(store.Record(Run("same", TestStatus.Failed))).Kind);
    }

    [Fact]
    public void Another_worktree_with_a_different_source_is_not_flaky_evidence()
    {
        var other = CreateLinkedWorktree("feature");
        using (var sibling = RunHistoryStore.Open(BaseDirectory, other, time: _time))
        {
            _ = sibling.Record(Run("theirs", TestStatus.Passed));
        }

        using var store = Open();

        Assert.Equal(FailureKind.Unknown, Assert.Single(store.Record(Run("mine", TestStatus.Failed))).Kind);
    }

    [Fact]
    public void An_unreadable_history_of_another_worktree_is_ignored()
    {
        var other = CreateLinkedWorktree("feature");
        var location = HistoryLocation.Resolve(BaseDirectory, other);
        _ = Directory.CreateDirectory(location.Directory);
        File.WriteAllText(location.FilePath, "{ not json");
        var messages = new List<string>();
        using var store = RunHistoryStore.Open(BaseDirectory, _solution, messages.Add, _time);
        store.SaveDelay = TimeSpan.FromMinutes(10);

        Assert.Equal(FailureKind.Unknown, Assert.Single(store.Record(Run("s1", TestStatus.Failed))).Kind);
        _ = store.GetFlakyTests();

        _ = Assert.Single(messages, m => m.Contains(location.FilePath, StringComparison.Ordinal));
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

    // Makes the fixture's worktree a git repository and adds a linked worktree of it; returns its solution path.
    private string CreateLinkedWorktree(string name)
    {
        var gitDir = Path.Combine(_root, "repo", ".git");
        var worktreeGitDir = Path.Combine(gitDir, "worktrees", name);
        _ = Directory.CreateDirectory(worktreeGitDir);
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..");
        var worktree = Path.Combine(_root, name);
        _ = Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}");
        var solution = Path.Combine(worktree, "App.slnx");
        File.WriteAllText(solution, "<Solution />");
        return solution;
    }

    private TestRunRecord Run(
        string source,
        TestStatus status,
        string? targetFramework = null,
        IReadOnlyList<TestStatus>? attempts = null,
        ResultFreshness freshness = ResultFreshness.Current)
    {
        _sequence++;
        _time.Now += TimeSpan.FromMinutes(1);
        var result = new TestResult(Test, "Adds", status, TimeSpan.FromMilliseconds(12.5), null,
            status == TestStatus.Failed ? "boom" : null, status == TestStatus.Failed ? "at Adds()" : null, null)
        {
            TargetFramework = targetFramework,
            Attempts = [.. (attempts ?? []).Select(a => new TestAttempt(a, TimeSpan.FromMilliseconds(10), a == TestStatus.Failed ? "boom" : null, null))],
        };
        return new TestRunRecord
        {
            RunId = $"run-{_sequence}",
            EngineInstanceId = "engine",
            Sequence = _sequence,
            Trigger = TestRunTrigger.Forced,
            Status = TestRunStatus.Completed,
            Freshness = freshness,
            RequestedAt = _time.Now,
            CompletedAt = _time.Now,
            SourceAfter = new SourceFingerprint("sha256", source, 1),
            Suites = [new TestSuite(Project, [result], _time.Now, result.Duration) { ProjectId = ProjectId }],
        };
    }

    private string LegacyFile() => """
        {
          "Version": 1,
          "Names": ["App.Tests", "App.Tests.CalculatorTests.Adds"],
          "Tests": [
            {
              "Project": 0,
              "Test": 1,
              "Runs": [
                { "RunId": "legacy-run", "At": "2025-01-01T00:00:00+00:00", "Status": "P", "DurationMs": 12.5 }
              ]
            }
          ],
          "Runs": [
            {
              "Summary": {
                "RunId": "legacy-run",
                "Trigger": 0,
                "Status": 0,
                "Scope": null,
                "IsFullScope": true,
                "Freshness": 0,
                "RequestedAt": "2025-01-01T00:00:00+00:00",
                "CompletedAt": "2025-01-01T00:00:00+00:00",
                "Source": null,
                "Passed": 1,
                "Failed": 0,
                "Skipped": 0
              },
              "Projects": [0],
              "Tests": [1],
              "Statuses": "P",
              "DurationsMs": [12.5]
            }
          ]
        }
        """;

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
