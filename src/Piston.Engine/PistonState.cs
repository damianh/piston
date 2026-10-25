// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine;

public sealed class PistonState
{
    public PistonPhase Phase { get; set; }
    public string? SolutionPath { get; set; }
    public BuildResult? LastBuild { get; set; }
    public IReadOnlyList<TestSuite> TestSuites { get; set; } = [];

    /// <summary>
    /// Live snapshot of test results during a running test phase.
    /// Tests start as <see cref="TestStatus.Running"/> and are updated as stdout lines arrive.
    /// Replaced atomically by <see cref="TestSuites"/> when the run completes.
    /// </summary>
    public IReadOnlyList<TestSuite> InProgressSuites { get; set; } = [];
    public DateTimeOffset? LastRunTime { get; set; }

    /// <summary>Duration of the most recent build (successful or failed).</summary>
    public TimeSpan? LastBuildDuration { get; set; }

    /// <summary>Wall-clock duration of the most recent test run.</summary>
    public TimeSpan? LastTestDuration { get; set; }

    /// <summary>
    /// Stderr output from the most recent dotnet-test invocation, if any.
    /// Populated when the run produces no TRX results (e.g. project failed to load).
    /// </summary>
    public string? LastTestRunnerError { get; set; }

    /// <summary>
    /// Optional substring or regex to filter which tests appear in the tree.
    /// Null means show all tests.
    /// </summary>
    public string? TestFilter { get; set; }

    // ── Build progress (populated by orchestrator during Building phase) ──

    /// <summary>Projects the current build is expected to compile; 0 when unknown.</summary>
    public int TotalBuildProjects { get; set; }

    /// <summary>Projects the current build has produced output for.</summary>
    public int CompletedBuildProjects { get; set; }

    /// <summary>Name of the project the current build most recently finished.</summary>
    public string? LastBuiltProject { get; set; }

    // ── Progress tracking (populated by orchestrator during Testing phase) ──

    /// <summary>Total number of tests expected in the current run. Set when entering Testing phase.</summary>
    public int TotalExpectedTests { get; set; }

    /// <summary>Number of tests that have completed (non-Running) in the current run.</summary>
    public int CompletedTests { get; set; }

    /// <summary>Total number of test projects in the current run.</summary>
    public int TotalTestProjects { get; set; }

    /// <summary>Number of test projects that have completed in the current run.</summary>
    public int CompletedTestProjects { get; set; }

    /// <summary>Per-project status: project path → completed/running/error.</summary>
    public IReadOnlyDictionary<string, ProjectRunStatus> ProjectStatuses { get; set; }
        = new Dictionary<string, ProjectRunStatus>();

    // ── Verified/stale tracking ──

    /// <summary>Timestamp of the most recent file change that triggered a run. Null until first file change.</summary>
    public DateTimeOffset? LastFileChangeTime { get; set; }

    /// <summary>The changed files that triggered the current/last run.</summary>
    public IReadOnlyList<string>? LastChangedFiles { get; set; }

    /// <summary>Projects affected by the last file change (populated during Analyzing phase).</summary>
    public IReadOnlyList<string>? AffectedProjects { get; set; }

    /// <summary>Test projects selected for execution (populated during Analyzing phase).</summary>
    public IReadOnlyList<string>? AffectedTestProjects { get; set; }

    /// <summary>Number of tests whose suite Timestamp is >= LastFileChangeTime (i.e. re-verified since last change).</summary>
    public int VerifiedSinceChangeCount { get; set; }

    // ── Coverage status ───────────────────────────────────────────────────────

    /// <summary>Whether coverage collection is active for this session.</summary>
    public bool CoverageEnabled { get; set; }

    /// <summary>Whether any coverage data has been stored in the current session.</summary>
    public bool HasCoverageData { get; set; }

    /// <summary>Human-readable Tier 3 impact detail, e.g. "Tier 3: 5 test(s) (coverage)". Null when using Tier 2.</summary>
    public string? CoverageImpactDetail { get; set; }

    /// <summary>Progress of the idle-time impact backfill; null when it is not running.</summary>
    public ImpactBackfillProgress? ImpactBackfill { get; set; }

    /// <summary>The test that has been running longest in the current run, or null when none is running.</summary>
    public RunningTest? LongestRunningTest { get; set; }

    /// <summary>What the current run's retries of its failed tests are doing, e.g. "Retrying 2 failed test(s): attempt 1 of 3"; null when not retrying.</summary>
    public string? RetryStatus { get; set; }

    // ── Run provenance ───────────────────────────────────────────────────────

    /// <summary>Number of terminal runs retained in <see cref="RecentRuns"/> (in memory only).</summary>
    public const int MaxRecentRuns = 16;

    /// <summary>The run currently executing (after it acquired the run lock), or null.</summary>
    public TestRunRecord? CurrentRun { get; set; }

    /// <summary>The most recently requested run that reached a terminal state.</summary>
    public TestRunRecord? LastRun { get; set; }

    /// <summary>Terminal runs, newest first, bounded by <see cref="MaxRecentRuns"/>.</summary>
    public IReadOnlyList<TestRunRecord> RecentRuns { get; set; } = [];

    /// <summary>Finds a retained run by ID, including the active run.</summary>
    public TestRunRecord? FindRun(string runId)
    {
        var current = CurrentRun;
        if (current?.RunId == runId)
        {
            return current;
        }

        return RecentRuns.FirstOrDefault(r => r.RunId == runId);
    }

    public int TotalPassed => TestSuites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatus.Passed);
    public int TotalFailed => TestSuites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatus.Failed);
    public int TotalSkipped => TestSuites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatus.Skipped);

    public event Action? StateChanged;

    public void NotifyChanged() => StateChanged?.Invoke();
}
