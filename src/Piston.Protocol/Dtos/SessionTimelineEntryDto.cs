// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>
/// Compact provenance of one terminal run in the daemon's session. Enum-like fields are strings so new
/// values can be added without breaking older clients. Lists are capped; the matching counts are totals.
/// </summary>
/// <param name="SourceBefore">Source fingerprint value before the run built; null when not captured.</param>
/// <param name="SourceAfter">Source fingerprint value after the run finished; null when not captured.</param>
/// <param name="ChangedFiles">Files whose change triggered the run.</param>
/// <param name="BuildStatus">How the run's build ended; null when it did not build.</param>
/// <param name="NewlyFailingTests">Tests that failed in this run but not before it.</param>
/// <param name="FixedTests">Tests that failed before this run and passed in it.</param>
/// <param name="FailureClassifications">How the run's failures relate to run history; empty when history is off.</param>
/// <param name="InRunHistory">True when the run is currently kept in the run history (the dashboard's Runs tab).</param>
/// <param name="NoEarlierResults">The run had no earlier results to compare with (the session's first results, or the
/// first after results were cleared), so its outcome changes are unknown rather than empty.</param>
public sealed record SessionTimelineEntryDto(
    string RunId,
    long Sequence,
    string Trigger,
    string Status,
    string? StatusReason,
    string? Scope,
    bool IsFullScope,
    bool IsFiltered,
    string Freshness,
    string? FreshnessReason,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? SourceBefore,
    string? SourceAfter,
    bool SourceChangeObserved,
    IReadOnlyList<string> ChangedFiles,
    int ChangedFileCount,
    string? BuildStatus,
    int BuildErrorCount,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<TestOutcomeReferenceDto> NewlyFailingTests,
    int NewlyFailingCount,
    IReadOnlyList<TestOutcomeReferenceDto> FixedTests,
    int FixedCount,
    IReadOnlyList<FailureClassificationDto> FailureClassifications,
    bool InRunHistory,
    bool NoEarlierResults = false);
