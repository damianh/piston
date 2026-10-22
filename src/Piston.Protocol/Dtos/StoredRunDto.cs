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
