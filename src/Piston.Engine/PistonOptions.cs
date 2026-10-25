// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine;

/// <summary>
/// Controls which test execution backend is used.
/// </summary>
public enum TestExecutionMode
{
    /// <summary>
    /// Use MTP v2 in-process when detected; fall back to process-based execution.
    /// </summary>
    Auto,

    /// <summary>
    /// Always use <c>dotnet test</c> process-based execution. Safe universal fallback.
    /// </summary>
    Process,

    /// <summary>
    /// Always use MTP v2 in-process execution. Fails if the project is incompatible.
    /// </summary>
    InProcess,
}

/// <summary>How finely the idle-time impact backfill measures which tests execute which lines.</summary>
public enum ImpactBackfillMode
{
    /// <summary>No backfill: tests are attributed to lines per test project.</summary>
    Off,

    /// <summary>Measure each test class separately when idle.</summary>
    Class,

    /// <summary>Measure each test method separately when idle; precise but slow.</summary>
    Test,
}

/// <summary>
/// Resolved runtime options — merged from CLI args, .piston.json, and defaults.
/// Passed into services at startup.
/// </summary>
public sealed class PistonOptions
{
    /// <summary>Absolute path to the .sln / .slnx / .slnf file to watch.</summary>
    public required string SolutionPath { get; init; }

    /// <summary>File-change debounce window before triggering a rebuild.</summary>
    public TimeSpan DebounceInterval { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Optional substring or regex pattern to filter which tests are shown.
    /// Null means show all tests.
    /// </summary>
    public string? TestFilter { get; init; }

    /// <summary>
    /// When true, enables code coverage collection during test runs via
    /// <c>--collect "XPlat Code Coverage"</c> (coverlet). Default is false (opt-in).
    /// </summary>
    public bool CoverageEnabled { get; init; } = false;

    /// <summary>
    /// When coverage is enabled, measures each test class (or test) separately while the engine is idle so a
    /// change runs only the tests that executed the changed lines. Default is <see cref="ImpactBackfillMode.Test"/>.
    /// </summary>
    public ImpactBackfillMode ImpactBackfill { get; init; } = ImpactBackfillMode.Test;

    /// <summary>
    /// How long a test runs before it is reported as possibly hanging (a warning only). Null turns the
    /// warning off. Default is 60 seconds.
    /// </summary>
    public TimeSpan? HangThreshold { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Per-test limit: a test that runs longer fails as timed out and its project's run is cancelled
    /// (other projects go on). Null (the default) turns it off.
    /// </summary>
    public TimeSpan? TestTimeout { get; init; }

    /// <summary>
    /// Maximum number of concurrent <c>dotnet test</c> processes.
    /// 0 means auto-detect: <c>Math.Max(1, Environment.ProcessorCount / 2)</c>.
    /// </summary>
    public int ProcessPoolSize { get; init; } = 0;

    /// <summary>
    /// Number of runs after which a pool slot is considered "stale" and logged as a warning.
    /// Default is 50.
    /// </summary>
    public int ProcessRecycleAfter { get; init; } = 50;

    /// <summary>
    /// Selects the test execution backend. Default is <see cref="TestExecutionMode.Auto"/>.
    /// </summary>
    public TestExecutionMode TestExecutionMode { get; init; } = TestExecutionMode.Auto;

    /// <summary>
    /// Memory in MiB each run reserves from the machine execution budget. Null reserves no memory;
    /// required when the machine budget limits memory. This is an admission reservation, not a cap.
    /// </summary>
    public int? ExecutionMemoryReservationMb { get; init; }

    /// <summary>
    /// Absolute path of the machine-wide execution coordination directory shared by daemons.
    /// Null uses <c>PISTON_EXECUTION_DIRECTORY</c> or the per-user default.
    /// </summary>
    public string? ExecutionCoordinationDirectory { get; init; }

    /// <summary>
    /// Keeps per-test run history outside the checkout and classifies failures as new, pre-existing or flaky.
    /// Default is true.
    /// </summary>
    public bool HistoryEnabled { get; init; } = true;

    /// <summary>Most retries a run makes of each failed test, from 0 to this.</summary>
    public const int MaxRetryFailedTests = 3;

    /// <summary>
    /// How many times a run re-runs its failed tests, without rebuilding, to gather flakiness evidence: 0 (default)
    /// turns retries off; at most <see cref="MaxRetryFailedTests"/>. A test that passes on a retry counts as passed
    /// and is reported as passed on retry; its earlier attempts are kept. A run with more than
    /// <see cref="Orchestration.PistonOrchestrator.MaxRetriedFailures"/> failed tests is not retried.
    /// </summary>
    public int RetryFailedTests { get; init; }

    /// <summary>
    /// Keeps each Microsoft.Testing.Platform test app running in server mode between runs, so later
    /// discoveries and runs skip host start-up. A host is replaced when its build output changes.
    /// Coverage runs always use a fresh process. Default is false.
    /// </summary>
    public bool WarmTestHosts { get; init; }

    /// <summary>
    /// Absolute base directory of the run history. Null uses <c>PISTON_HISTORY_DIRECTORY</c> or the per-user default.
    /// </summary>
    public string? HistoryDirectory { get; init; }

    /// <summary>
    /// Recorded in the worktree ownership details so tools can tell a <c>piston mcp</c> session from a daemon.
    /// </summary>
    public string? OwnerHost { get; init; }
}
