// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

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
