// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Verification;

/// <summary>
/// Pure decision rules for current full verification. A result counts only when it is a terminal,
/// full-scope, unfiltered run whose post-run source fingerprint matches the present source.
/// </summary>
public static class VerificationPolicy
{
    /// <summary>Decides the next step from a snapshot of engine state.</summary>
    /// <param name="current">Present source fingerprint; null when it could not be computed.</param>
    /// <param name="active">The run currently queued or executing, if any.</param>
    /// <param name="recentRuns">Terminal runs, newest first.</param>
    /// <param name="userFilter">The user test filter currently configured.</param>
    /// <param name="hasPendingSourceChanges">True when the watcher holds changes that have not triggered a run yet.</param>
    public static VerificationDecision Decide(
        SourceFingerprint? current,
        TestRunRecord? active,
        IReadOnlyList<TestRunRecord> recentRuns,
        string? userFilter,
        bool hasPendingSourceChanges)
    {
        if (!string.IsNullOrWhiteSpace(userFilter))
        {
            return new VerificationDecision(
                VerificationAction.Final,
                Verdict: VerificationVerdict.NotFullScope,
                Reason: $"Test filter '{userFilter}' is active; clear it (set_test_filter with an empty string) for full verification.");
        }

        if (active is not null)
        {
            return IsAwaitable(active)
                ? new VerificationDecision(VerificationAction.AwaitActive, active)
                : new VerificationDecision(VerificationAction.RequestForced);
        }

        if (current is null || hasPendingSourceChanges)
        {
            return new VerificationDecision(VerificationAction.RequestForced);
        }

        // Only the newest full-scope result for this exact source is considered, so an older pass
        // never hides a newer failure for the same source.
        var newest = recentRuns.FirstOrDefault(r =>
            r.IsFullScope && r.Freshness == ResultFreshness.Current && r.SourceAfter == current);

        return newest is not null && IsReusable(newest)
            ? new VerificationDecision(VerificationAction.Reuse, newest)
            : new VerificationDecision(VerificationAction.RequestForced);
    }

    /// <summary>Evaluates a terminal run against the present source fingerprint.</summary>
    public static VerificationEvaluation Evaluate(TestRunRecord run, SourceFingerprint? current)
    {
        switch (run.Status)
        {
            case TestRunStatus.Faulted:
                return new(VerificationVerdict.Faulted, false, run.StatusReason ?? "The run faulted.");
            case TestRunStatus.Cancelled:
                return new(VerificationVerdict.Cancelled, true, run.StatusReason ?? "The run was cancelled.");
            case TestRunStatus.Queued or TestRunStatus.Running:
                return new(VerificationVerdict.Cancelled, true, "The run did not reach a terminal state.");
        }

        if (run.IsFiltered || run.Scope != TestRunScope.Full)
        {
            return new(VerificationVerdict.NotFullScope, false,
                run.IsFiltered
                    ? $"Run was filtered ('{run.Context?.EffectiveFilter}'); it does not cover the full scope."
                    : "Run was selective; it does not cover the full scope.");
        }

        if (run.Freshness != ResultFreshness.Current)
        {
            return new(VerificationVerdict.Stale, true,
                run.FreshnessReason ?? $"Run freshness is {run.Freshness}.");
        }

        if (current is null)
        {
            return new(VerificationVerdict.Stale, true, "Present source fingerprint could not be computed.");
        }

        if (run.SourceAfter != current)
        {
            return new(VerificationVerdict.Stale, true, "Source changed after the run finished.");
        }

        switch (run.Status)
        {
            case TestRunStatus.BuildFailed:
                return new(VerificationVerdict.BuildFailed, false, run.StatusReason ?? "Build failed.");
            case TestRunStatus.Incomplete:
                return new(VerificationVerdict.Incomplete, false, run.StatusReason ?? "Run was incomplete.");
        }

        if (run.DuplicateTestIds.Count > 0)
        {
            return new(VerificationVerdict.Incomplete, false,
                $"{run.DuplicateTestIds.Count} duplicate test ID(s); results cannot be attributed reliably.");
        }

        var failed = run.Tests.Count(t => t.Status == TestStatus.Failed);
        return failed > 0
            ? new(VerificationVerdict.TestsFailed, false, $"{failed} test(s) failed.")
            : new(VerificationVerdict.Passed, false, null);
    }

    /// <summary>An active run that will produce a full-scope, unfiltered result.</summary>
    internal static bool IsAwaitable(TestRunRecord active) =>
        !active.IsFiltered
        && (active.Scope == TestRunScope.Full
            || (active.Scope is null && active.Trigger != TestRunTrigger.FileChange));

    private static bool IsReusable(TestRunRecord run) =>
        (run.Status == TestRunStatus.Completed && run.DuplicateTestIds.Count == 0)
        || run.Status == TestRunStatus.BuildFailed;
}
