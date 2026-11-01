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
