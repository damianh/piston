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
public sealed record FailureClassification(string Project, string Test, FailureKind Kind, string Evidence);

/// <summary>One recorded result of a test.</summary>
/// <param name="RunId">The run that produced it.</param>
/// <param name="At">When the run finished.</param>
/// <param name="Status">Passed, Failed or Skipped.</param>
/// <param name="DurationMs">Duration in milliseconds.</param>
/// <param name="Source">The run's source fingerprint when its results were current; null otherwise.</param>
public sealed record TestHistoryEntry(string RunId, DateTimeOffset At, TestStatus Status, double DurationMs, string? Source);

/// <summary>Recorded results of one test, oldest first.</summary>
public sealed record TestHistory(string Project, string Test, IReadOnlyList<TestHistoryEntry> Runs, bool Flaky);

/// <summary>Summary of a run kept in the history.</summary>
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
    int Skipped);

/// <summary>One test's result in a stored run. Messages and stack traces are kept for failures only, clipped.</summary>
public sealed record StoredTestResult(
    string Project,
    string Test,
    TestStatus Status,
    double DurationMs,
    string? ErrorMessage = null,
    string? StackTrace = null);

/// <summary>A run kept in the history with its per-test results.</summary>
public sealed record StoredRun(StoredRunSummary Summary, IReadOnlyList<StoredTestResult> Results);
