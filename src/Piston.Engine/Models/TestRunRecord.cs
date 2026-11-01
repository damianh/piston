// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>
/// Provenance and results of exactly one test run. Unlike the aggregate
/// <see cref="PistonState.TestSuites"/>, <see cref="Suites"/> contains only tests executed by this run.
/// </summary>
public sealed record TestRunRecord
{
    public required string RunId { get; init; }

    /// <summary>The solution being verified, recorded before admission or execution context is available.</summary>
    public string? SolutionPath { get; init; }

    /// <summary>Identifies the engine instance; <see cref="Sequence"/> is only ordered within one instance.</summary>
    public required string EngineInstanceId { get; init; }

    /// <summary>Monotonically increasing request order within an engine instance.</summary>
    public required long Sequence { get; init; }

    public required TestRunTrigger Trigger { get; init; }
    public TestRunStatus Status { get; init; } = TestRunStatus.Queued;

    /// <summary>True when restored from a prior daemon session and not re-verified since restart.</summary>
    public bool Restored { get; init; }

    /// <summary>Human-readable reason for a non-<see cref="TestRunStatus.Completed"/> status.</summary>
    public string? StatusReason { get; init; }

    public TestRunScope? Scope { get; init; }
    public ResultFreshness Freshness { get; init; } = ResultFreshness.Unknown;
    public string? FreshnessReason { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Fingerprint captured before analysis/build.</summary>
    public SourceFingerprint? SourceBefore { get; init; }

    /// <summary>Fingerprint captured after the run finished.</summary>
    public SourceFingerprint? SourceAfter { get; init; }

    /// <summary>True when the file watcher reported a change while this run was executing.</summary>
    public bool SourceChangeObserved { get; init; }

    public TestRunContext? Context { get; init; }

    /// <summary>
    /// True when, before this run built, every evaluated build input was inside the source fingerprint.
    /// Only such runs can be bound to a later fingerprint, because inputs outside it can change unseen.
    /// </summary>
    public bool InputCoverageVerified { get; init; }

    /// <summary>Build output from this run only; null if its build did not return a result.</summary>
    public BuildResult? Build { get; init; }

    public IReadOnlyList<TestRunProjectOutcome> Projects { get; init; } = [];

    /// <summary>Suites produced by this run only (never merged with earlier runs).</summary>
    public IReadOnlyList<TestSuite> Suites { get; init; } = [];

    /// <summary>Fully-qualified names reported more than once by this run.</summary>
    public IReadOnlyList<string> DuplicateTestIds { get; init; } = [];

    /// <summary>Tests that ran longer than the hang threshold during this run.</summary>
    public IReadOnlyList<string> PossiblyHangingTests { get; init; } = [];

    /// <summary>The files whose change triggered this run; empty unless it was triggered by a file change.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    /// <summary>Project-scoped result keys of tests that failed before this run and passed in it.</summary>
    public IReadOnlyList<TestOutcomeReference> FixedTests { get; init; } = [];

    /// <summary>
    /// Result keys of tests that failed in this run but had not failed before it (including new tests);
    /// empty when there were no earlier results to compare with.
    /// </summary>
    public IReadOnlyList<TestOutcomeReference> NewlyFailingTests { get; init; } = [];

    /// <summary>
    /// The run produced test results but there were no earlier results to compare them with (the session's first
    /// results, or the first after results were cleared), so <see cref="FixedTests"/> and
    /// <see cref="NewlyFailingTests"/> are empty whatever changed.
    /// </summary>
    public bool NoEarlierResults { get; init; }

    /// <summary>
    /// How each failed test relates to the worktree's run history (new, pre-existing, flaky or unknown);
    /// tests that failed and then passed on retry are included as flaky. Empty when history is off or nothing failed.
    /// </summary>
    public IReadOnlyList<FailureClassification> FailureClassifications { get; init; } = [];

    /// <summary>Most retries of each failed test; 0 when retries are off.</summary>
    public int RetryLimit { get; init; }

    /// <summary>Why failed tests were not retried although retries are on (for example, too many failed).</summary>
    public string? RetriesSkippedReason { get; init; }

    public string? RunnerError { get; init; }

    /// <summary>Machine-wide admission; null when the engine runs without machine coordination.</summary>
    public TestRunAdmission? Admission { get; init; }

    public bool IsTerminal => Status is not (TestRunStatus.Queued or TestRunStatus.Running);
    public bool IsFiltered => Context?.EffectiveFilter is not null;

    /// <summary>All test projects ran without any test filter.</summary>
    public bool IsFullScope => Scope == TestRunScope.Full && !IsFiltered;

    /// <summary>
    /// The run completed, covered full scope without filters, reported unique test identities and
    /// its source inputs did not change during execution. Callers must still compare
    /// <see cref="SourceAfter"/> with the generation they expect.
    /// </summary>
    public bool IsCurrentFullResult =>
        Status == TestRunStatus.Completed
        && !Restored
        && IsFullScope
        && Freshness == ResultFreshness.Current
        && DuplicateTestIds.Count == 0
        && !Tests.Any(t => t.Status is TestStatus.NotRun or TestStatus.Running);

    public IEnumerable<TestResult> Tests => Suites.SelectMany(s => s.Tests);
}
