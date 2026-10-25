// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>How a failing test relates to its recorded history in this worktree.</summary>
public enum FailureKind
{
    /// <summary>No earlier pass or failure is recorded, so whether the failure is new is unknown.</summary>
    Unknown,

    /// <summary>The previous recorded result passed.</summary>
    New,

    /// <summary>The previous recorded result also failed.</summary>
    PreExisting,

    /// <summary>The test both passed and failed on the same source fingerprint within the recent history.</summary>
    Flaky,
}

/// <summary>Classification of one failed test in a run, with the evidence it rests on.</summary>
/// <param name="Project">Test project name (suite name).</param>
/// <param name="Test">Fully-qualified name, with the target framework when multi-targeted.</param>
/// <param name="Kind">The classification.</param>
/// <param name="Evidence">One sentence naming the earlier runs it is based on.</param>
/// <param name="Flakiness">The outcomes that make the test flaky; set when <paramref name="Kind"/> is <see cref="FailureKind.Flaky"/>.</param>
public sealed record FailureClassification(string Project, string Test, FailureKind Kind, string Evidence, FlakinessEvidence? Flakiness = null);

/// <summary>One recorded outcome that a flaky label rests on.</summary>
/// <param name="RunId">The run that produced it.</param>
/// <param name="At">When the run finished.</param>
/// <param name="Status">Passed or Failed.</param>
/// <param name="Attempt">0 for a run's first attempt; n for its n-th retry.</param>
/// <param name="Worktree">The other worktree that recorded it; null for this worktree.</param>
public sealed record FlakyOutcome(string RunId, DateTimeOffset At, TestStatus Status, int Attempt, string? Worktree);

/// <summary>
/// Why a test is labelled flaky: it both passed and failed on one source fingerprint (in this worktree or another
/// worktree of the repository), or within one run's retries of the same build.
/// </summary>
/// <param name="Source">The source fingerprint both outcomes ran against; null when the evidence is one run's retries without a current fingerprint.</param>
/// <param name="Passed">Passes recorded on that source (or in that run).</param>
/// <param name="Failed">Failures recorded on that source (or in that run).</param>
/// <param name="RetryOutcomes">How many of those outcomes came from in-run retries.</param>
/// <param name="OtherWorktrees">Other worktrees of the repository that contributed outcomes, by folder name.</param>
/// <param name="Outcomes">The outcomes, newest first, capped at <see cref="MaxOutcomes"/>.</param>
public sealed record FlakinessEvidence(
    string? Source,
    int Passed,
    int Failed,
    int RetryOutcomes,
    IReadOnlyList<string> OtherWorktrees,
    IReadOnlyList<FlakyOutcome> Outcomes)
{
    /// <summary>Most outcomes listed in <see cref="Outcomes"/>.</summary>
    public const int MaxOutcomes = 10;

    /// <summary>"Passed 3 and failed 2 times on source 0123456789ab (1 from in-run retries; other worktrees: a, b)."</summary>
    public string Describe()
    {
        var where = Source is { } source
            ? $"on source {(source.Length > 12 ? source[..12] : source)}"
            : $"within run {Outcomes.FirstOrDefault()?.RunId} (retries of the same build)";
        var details = new List<string>();
        if (RetryOutcomes > 0)
        {
            details.Add($"{RetryOutcomes} from in-run retries");
        }

        if (OtherWorktrees.Count > 0)
        {
            details.Add($"other worktrees: {string.Join(", ", OtherWorktrees)}");
        }

        return $"Passed {Passed} and failed {Failed} time(s) {where}{(details.Count > 0 ? $" ({string.Join("; ", details)})" : "")}.";
    }

    /// <summary>"3 passed/2 failed on 0123456789ab, 1 from retries, other worktrees: a, b".</summary>
    public string Brief()
    {
        var text = $"{Passed} passed/{Failed} failed {(Source is { } source ? $"on {(source.Length > 12 ? source[..12] : source)}" : "in retries of one run")}";
        if (Source is not null && RetryOutcomes > 0)
        {
            text += $", {RetryOutcomes} from retries";
        }

        return OtherWorktrees.Count > 0 ? $"{text}, other worktrees: {string.Join(", ", OtherWorktrees)}" : text;
    }
}

/// <summary>One recorded result of a test.</summary>
/// <param name="RunId">The run that produced it.</param>
/// <param name="At">When the run finished.</param>
/// <param name="Status">Passed, Failed or Skipped.</param>
/// <param name="DurationMs">Duration in milliseconds.</param>
/// <param name="Source">The run's source fingerprint when its results were current; null otherwise.</param>
/// <param name="Attempt">0 for the run's first attempt; n for its n-th retry of the test.</param>
public sealed record TestHistoryEntry(string RunId, DateTimeOffset At, TestStatus Status, double DurationMs, string? Source, int Attempt = 0);

/// <summary>Recorded results of one test, oldest first.</summary>
/// <param name="Flakiness">Why the test is flaky; null when it is not.</param>
public sealed record TestHistory(string Project, string Test, IReadOnlyList<TestHistoryEntry> Runs, FlakinessEvidence? Flakiness)
{
    public bool Flaky => Flakiness is not null;
}

/// <summary>Summary of a run kept in the history.</summary>
/// <param name="Retried">Tests that failed and were retried within the run.</param>
/// <param name="PassedOnRetry">Retried tests that passed on a retry (counted in <paramref name="Passed"/>).</param>
public sealed record StoredRunSummary(
    string RunId,
    TestRunTrigger Trigger,
    TestRunStatus Status,
    TestRunScope? Scope,
    bool IsFullScope,
    ResultFreshness Freshness,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    string? Source,
    int Passed,
    int Failed,
    int Skipped,
    int Retried = 0,
    int PassedOnRetry = 0);

/// <summary>An earlier attempt of a test that was retried within a stored run. The message is kept for failures only, clipped.</summary>
public sealed record StoredAttempt(TestStatus Status, double DurationMs, string? ErrorMessage = null);

/// <summary>One test's result in a stored run. Messages and stack traces are kept for failures only, clipped.</summary>
/// <param name="Attempts">Earlier attempts in the run, oldest first, when the test was retried; null otherwise.</param>
public sealed record StoredTestResult(
    string Project,
    string Test,
    TestStatus Status,
    double DurationMs,
    string? ErrorMessage = null,
    string? StackTrace = null,
    IReadOnlyList<StoredAttempt>? Attempts = null);

/// <summary>A run kept in the history with its per-test results.</summary>
public sealed record StoredRun(StoredRunSummary Summary, IReadOnlyList<StoredTestResult> Results);
