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

    // ── Progress tracking (populated by orchestrator during Testing phase) ──

    /// <summary>Total number of tests expected in the current run. Set when entering Testing phase.</summary>
    public int TotalExpectedTests { get; set; }

    /// <summary>Number of tests that have completed (non-Running) in the current run.</summary>
    public int CompletedTests { get; set; }

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

    public int TotalPassed => TestSuites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatus.Passed);
    public int TotalFailed => TestSuites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatus.Failed);
    public int TotalSkipped => TestSuites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatus.Skipped);

    public event Action? StateChanged;

    public void NotifyChanged() => StateChanged?.Invoke();
}
