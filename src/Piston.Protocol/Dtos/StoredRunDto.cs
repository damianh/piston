// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A run kept in the worktree's run history.</summary>
/// <param name="Source">Source fingerprint value; null unless the run's results were current.</param>
/// <param name="Retried">Tests that failed and were retried within the run.</param>
/// <param name="PassedOnRetry">Retried tests that passed on a retry (counted in <paramref name="Passed"/>).</param>
public sealed record StoredRunSummaryDto(
    string RunId,
    string Trigger,
    string Status,
    string? Scope,
    bool IsFullScope,
    string Freshness,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    string? Source,
    int Passed,
    int Failed,
    int Skipped,
    int Retried = 0,
    int PassedOnRetry = 0);

/// <summary>The runs kept in the run history, newest first.</summary>
/// <param name="Enabled">False when the daemon keeps no run history.</param>
public sealed record StoredRunsDto(bool Enabled, IReadOnlyList<StoredRunSummaryDto> Runs);

/// <summary>One test's result in a stored run.</summary>
/// <param name="Project">Test project name.</param>
/// <param name="Test">Fully-qualified name, with <c> [tfm]</c> when multi-targeted.</param>
/// <param name="Attempts">Earlier attempts of a test retried within the run, oldest first; null when it ran once.</param>
public sealed record StoredTestResultDto(
    string Project,
    string Test,
    string Status,
    double DurationMs,
    string? ErrorMessage,
    string? StackTrace,
    IReadOnlyList<StoredAttemptDto>? Attempts = null);

/// <summary>An earlier attempt of a test retried within a stored run.</summary>
public sealed record StoredAttemptDto(string Status, double DurationMs, string? ErrorMessage);

/// <summary>A stored run with its per-test results.</summary>
public sealed record StoredRunDto(StoredRunSummaryDto Summary, IReadOnlyList<StoredTestResultDto> Results);

/// <summary>What the run history says about a test that is failing now or flaky.</summary>
/// <param name="FailureKind">New, PreExisting, Flaky or Unknown when the test's latest result failed; otherwise null.</param>
/// <param name="Evidence">The earlier runs the failure kind rests on.</param>
/// <param name="Flaky">True when the test passed and failed on the same source within its recorded history.</param>
/// <param name="Flakiness">Why the test is flaky; null when it is not.</param>
public sealed record TestMarkDto(string Project, string Test, string? FailureKind, string? Evidence, bool Flaky, FlakyEvidenceDto? Flakiness = null);

/// <summary>The outcomes behind a flaky label.</summary>
/// <param name="Source">The source fingerprint the test passed and failed on; null when only retries within one run disagree.</param>
/// <param name="Passed">Passing outcomes counted.</param>
/// <param name="Failed">Failing outcomes counted.</param>
/// <param name="RetryOutcomes">Outcomes counted that came from in-run retries.</param>
/// <param name="OtherWorktrees">Other worktrees of the repository whose outcomes are counted.</param>
/// <param name="Summary">One sentence describing the evidence.</param>
/// <param name="Outcomes">The newest counted outcomes, newest first.</param>
public sealed record FlakyEvidenceDto(
    string? Source,
    int Passed,
    int Failed,
    int RetryOutcomes,
    IReadOnlyList<string> OtherWorktrees,
    string Summary,
    IReadOnlyList<FlakyOutcomeDto> Outcomes);

/// <summary>One outcome counted as flakiness evidence.</summary>
/// <param name="Attempt">0 for the run's first attempt; n for its n-th retry.</param>
/// <param name="Worktree">The other worktree that recorded it; null for this worktree.</param>
public sealed record FlakyOutcomeDto(string RunId, DateTimeOffset At, string Status, int Attempt, string? Worktree);

/// <summary>History marks for the tests that have any.</summary>
/// <param name="Enabled">False when the daemon keeps no run history.</param>
public sealed record TestMarksDto(bool Enabled, IReadOnlyList<TestMarkDto> Tests);

/// <summary>One recorded result of a test.</summary>
/// <param name="Source">The run's source fingerprint when its results were current; otherwise null.</param>
/// <param name="Attempt">0 for the run's first attempt; n for its n-th retry of the test.</param>
public sealed record TestHistoryEntryDto(string RunId, DateTimeOffset At, string Status, double DurationMs, string? Source, int Attempt = 0);

/// <summary>Recorded results of one test, oldest first.</summary>
/// <param name="Flakiness">Why the test is flaky; null when it is not.</param>
public sealed record TestHistoryDto(string Project, string Test, bool Flaky, IReadOnlyList<TestHistoryEntryDto> Runs, FlakyEvidenceDto? Flakiness = null);

/// <summary>Recorded results of the tests matching a name.</summary>
/// <param name="Enabled">False when the daemon keeps no run history.</param>
public sealed record TestHistoriesDto(bool Enabled, IReadOnlyList<TestHistoryDto> Tests);
