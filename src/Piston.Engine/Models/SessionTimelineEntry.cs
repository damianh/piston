// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>
/// Compact provenance of one terminal run in the engine's session timeline: what triggered it, the
/// source fingerprints it ran against, how it ended and which tests it broke or fixed. Unlike
/// <see cref="TestRunRecord"/> it holds no per-test results, so many runs can be kept in memory.
/// </summary>
/// <param name="SourceBefore">Fingerprint value captured before the run built; null when it did not start or fingerprinting failed.</param>
/// <param name="SourceAfter">Fingerprint value captured after the run finished; null when not captured.</param>
/// <param name="ChangedFiles">The files whose change triggered the run, at most <see cref="MaxListItems"/>.</param>
/// <param name="ChangedFileCount">How many files triggered the run, including any beyond <see cref="ChangedFiles"/>.</param>
/// <param name="BuildStatus">How the run's build ended; null when it did not build.</param>
/// <param name="NewlyFailingTests">Tests that failed in this run but not before it, at most <see cref="MaxListItems"/>.</param>
/// <param name="NewlyFailingCount">How many tests newly failed, including any beyond <see cref="NewlyFailingTests"/>.</param>
/// <param name="FixedTests">Tests that failed before this run and passed in it, at most <see cref="MaxListItems"/>.</param>
/// <param name="FixedCount">How many tests the run fixed, including any beyond <see cref="FixedTests"/>.</param>
/// <param name="FailureClassifications">How the run's failures relate to the run history, at most <see cref="MaxListItems"/>; empty when history is off.</param>
/// <param name="InRunHistory">True when the run was kept in the persistent run history, so it can be opened there.</param>
public sealed record SessionTimelineEntry(
    string RunId,
    long Sequence,
    TestRunTrigger Trigger,
    TestRunStatus Status,
    string? StatusReason,
    TestRunScope? Scope,
    bool IsFullScope,
    bool IsFiltered,
    ResultFreshness Freshness,
    string? FreshnessReason,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? SourceBefore,
    string? SourceAfter,
    bool SourceChangeObserved,
    IReadOnlyList<string> ChangedFiles,
    int ChangedFileCount,
    BuildStatus? BuildStatus,
    int BuildErrorCount,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<TestOutcomeReference> NewlyFailingTests,
    int NewlyFailingCount,
    IReadOnlyList<TestOutcomeReference> FixedTests,
    int FixedCount,
    IReadOnlyList<FailureClassification> FailureClassifications,
    bool InRunHistory)
{
    /// <summary>Upper bound on each list an entry keeps, so one huge run cannot dominate memory.</summary>
    public const int MaxListItems = 200;

    /// <summary>Summarises a terminal run.</summary>
    public static SessionTimelineEntry From(TestRunRecord run, bool inRunHistory)
    {
        var tests = run.Tests.ToList();
        return new SessionTimelineEntry(
            run.RunId,
            run.Sequence,
            run.Trigger,
            run.Status,
            run.StatusReason,
            run.Scope,
            run.IsFullScope,
            run.IsFiltered,
            run.Freshness,
            run.FreshnessReason,
            run.RequestedAt,
            run.StartedAt,
            run.CompletedAt,
            run.SourceBefore?.Value,
            run.SourceAfter?.Value,
            run.SourceChangeObserved,
            [.. run.ChangedFiles.Take(MaxListItems)],
            run.ChangedFiles.Count,
            run.Build?.Status,
            run.Build?.Errors.Count ?? 0,
            tests.Count(t => t.Status == TestStatus.Passed),
            tests.Count(t => t.Status == TestStatus.Failed),
            tests.Count(t => t.Status == TestStatus.Skipped),
            [.. run.NewlyFailingTests.Take(MaxListItems)],
            run.NewlyFailingTests.Count,
            [.. run.FixedTests.Take(MaxListItems)],
            run.FixedTests.Count,
            [.. run.FailureClassifications.Take(MaxListItems)],
            inRunHistory);
    }
}
