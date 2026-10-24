// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A run kept in the worktree's run history.</summary>
/// <param name="Source">Source fingerprint value; null unless the run's results were current.</param>
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
    int Skipped);

/// <summary>The runs kept in the run history, newest first.</summary>
/// <param name="Enabled">False when the daemon keeps no run history.</param>
public sealed record StoredRunsDto(bool Enabled, IReadOnlyList<StoredRunSummaryDto> Runs);

/// <summary>One test's result in a stored run.</summary>
/// <param name="Project">Test project name.</param>
/// <param name="Test">Fully-qualified name, with <c> [tfm]</c> when multi-targeted.</param>
public sealed record StoredTestResultDto(
    string Project,
    string Test,
    string Status,
    double DurationMs,
    string? ErrorMessage,
    string? StackTrace);

/// <summary>A stored run with its per-test results.</summary>
public sealed record StoredRunDto(StoredRunSummaryDto Summary, IReadOnlyList<StoredTestResultDto> Results);

/// <summary>What the run history says about a test that is failing now or flaky.</summary>
/// <param name="FailureKind">New, PreExisting, Flaky or Unknown when the test's latest result failed; otherwise null.</param>
/// <param name="Evidence">The earlier runs the failure kind rests on.</param>
/// <param name="Flaky">True when the test passed and failed on the same source within its recorded history.</param>
public sealed record TestMarkDto(string Project, string Test, string? FailureKind, string? Evidence, bool Flaky);

/// <summary>History marks for the tests that have any.</summary>
/// <param name="Enabled">False when the daemon keeps no run history.</param>
public sealed record TestMarksDto(bool Enabled, IReadOnlyList<TestMarkDto> Tests);

/// <summary>One recorded result of a test.</summary>
/// <param name="Source">The run's source fingerprint when its results were current; otherwise null.</param>
public sealed record TestHistoryEntryDto(string RunId, DateTimeOffset At, string Status, double DurationMs, string? Source);

/// <summary>Recorded results of one test, oldest first.</summary>
public sealed record TestHistoryDto(string Project, string Test, bool Flaky, IReadOnlyList<TestHistoryEntryDto> Runs);

/// <summary>Recorded results of the tests matching a name.</summary>
/// <param name="Enabled">False when the daemon keeps no run history.</param>
public sealed record TestHistoriesDto(bool Enabled, IReadOnlyList<TestHistoryDto> Tests);
