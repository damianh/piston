// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>A test result key scoped to the project suite that produced it.</summary>
public sealed record TestOutcomeReference(string Project, string Key);

/// <summary>What caused a test run to be requested.</summary>
public enum TestRunTrigger
{
    /// <summary>The initial run issued when the engine starts.</summary>
    Startup,

    /// <summary>An explicit full run request (MCP <c>RunTests</c>, protocol force-run).</summary>
    Forced,

    /// <summary>A file-watcher change batch (normally a selective run).</summary>
    FileChange,

    /// <summary>A request to run selected tests, such as a node in the dashboard's test tree.</summary>
    Targeted,
}

/// <summary>Lifecycle state of a single test run.</summary>
public enum TestRunStatus
{
    /// <summary>
    /// Requested but not started: waiting for this daemon's previous run to finish, or (when
    /// <see cref="TestRunRecord.Admission"/> is set) queued for machine-wide execution admission.
    /// </summary>
    Queued,

    /// <summary>Analyzing, building or testing.</summary>
    Running,

    /// <summary>Every selected test project reported results.</summary>
    Completed,

    /// <summary>Tests ran, but at least one selected project crashed, failed to load or produced no results.</summary>
    Incomplete,

    /// <summary>The build failed; no tests ran.</summary>
    BuildFailed,

    /// <summary>Cancelled or superseded by a newer run before completing.</summary>
    Cancelled,

    /// <summary>The pipeline threw an unexpected exception.</summary>
    Faulted,
}

/// <summary>Which test projects a run selected.</summary>
public enum TestRunScope
{
    /// <summary>All test projects in the solution.</summary>
    Full,

    /// <summary>Only the test projects affected by a change.</summary>
    Selective,
}

/// <summary>Whether a run's results correspond to the source inputs present when it finished.</summary>
public enum ResultFreshness
{
    /// <summary>Freshness could not be established (fingerprint unavailable or run did not finish).</summary>
    Unknown,

    /// <summary>Source inputs were identical before and after execution and no change was observed.</summary>
    Current,

    /// <summary>Source inputs changed while the run was executing; results must not be treated as current.</summary>
    Stale,
}

/// <summary>Deterministic content fingerprint of the source inputs a run executed against.</summary>
/// <param name="Algorithm">Fingerprint algorithm identifier, e.g. <c>piston-source-v2</c>.</param>
/// <param name="Value">Lower-case hex SHA-256 digest.</param>
/// <param name="FileCount">Number of files included.</param>
public sealed record SourceFingerprint(string Algorithm, string Value, int FileCount);

/// <summary>Build and selection context captured when a run started.</summary>
/// <param name="SolutionPath">Solution the run was issued for.</param>
/// <param name="TestProjects">Selected test projects; null means the solution was passed to the runner as a single target.</param>
/// <param name="BuildTargets">Projects built before testing; null means the whole solution was built.</param>
/// <param name="UserFilter">The user-configured test filter at run start.</param>
/// <param name="EffectiveFilter">The filter actually passed to <c>dotnet test</c> (user filter combined with any coverage selection).</param>
/// <param name="CoverageSelectedTestCount">Number of tests selected by coverage impact analysis, when that narrowed the run.</param>
/// <param name="CoverageEnabled">Whether coverage collection was enabled.</param>
/// <param name="BuildConfiguration">Piston does not pass <c>-c</c>, so the .NET CLI default configuration applies.</param>
/// <param name="SdkVersion">Output of <c>dotnet --version</c> in the solution directory, when available.</param>
/// <param name="Runtime">Runtime hosting the Piston engine.</param>
/// <param name="OperatingSystem">Host operating system description.</param>
/// <param name="WorkingDirectory">Working directory of the build process and SDK version probe.</param>
/// <param name="TestWorkingDirectories">Working directory per test target: solution directory for MTP, project directory for VSTest.</param>
/// <param name="SelectionExplanation">One sentence saying why the run has this scope.</param>
/// <param name="SelectionReasons">
/// Why each test was selected, when the impact map narrowed the run to tests by changed lines.
/// </param>
public sealed record TestRunContext(
    string SolutionPath,
    IReadOnlyList<string>? TestProjects,
    IReadOnlyList<string>? BuildTargets,
    string? UserFilter,
    string? EffectiveFilter,
    int? CoverageSelectedTestCount,
    bool CoverageEnabled,
    string BuildConfiguration,
    string? SdkVersion,
    string Runtime,
    string OperatingSystem,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? TestWorkingDirectories = null,
    string? SelectionExplanation = null,
    IReadOnlyDictionary<string, IReadOnlyList<TestSelectionReason>>? SelectionReasons = null);

/// <summary>Machine-wide admission state of a run.</summary>
public enum TestRunAdmissionState
{
    /// <summary>Waiting in the machine-wide FIFO queue.</summary>
    Queued,

    /// <summary>Admitted; the run's reservation counts against the machine budget until it ends.</summary>
    Admitted,
}

/// <summary>
/// Machine-wide execution admission for a run, shared across Piston daemons of the same user.
/// Counts of other runs are a snapshot from the last queue evaluation (while queued) or from admission.
/// </summary>
/// <param name="State">Queued or admitted.</param>
/// <param name="ReservedCpuSlots">CPU slots this run reserves (the daemon's test-process parallelism).</param>
/// <param name="ReservedMemoryMb">Memory this run reserves in MiB, when configured.</param>
/// <param name="BudgetCpuSlots">Machine CPU slot budget.</param>
/// <param name="BudgetMemoryMb">Machine memory budget in MiB; null when memory is not budgeted.</param>
/// <param name="BudgetSource">Budget file path, or <c>default</c>.</param>
/// <param name="QueuedAhead">Runs queued ahead of this one (0 once admitted).</param>
/// <param name="QueueLength">Runs waiting in the queue, including this one while queued.</param>
/// <param name="OtherAdmittedRuns">Other admitted runs holding reservations.</param>
/// <param name="CpuSlotsInUse">CPU slots reserved by other admitted runs.</param>
/// <param name="MemoryMbInUse">Memory reserved by other admitted runs, in MiB.</param>
/// <param name="QueuedAt">When this run joined the queue (or inherited its position).</param>
/// <param name="AdmittedAt">When the run was admitted.</param>
public sealed record TestRunAdmission(
    TestRunAdmissionState State,
    int ReservedCpuSlots,
    int? ReservedMemoryMb,
    int BudgetCpuSlots,
    int? BudgetMemoryMb,
    string BudgetSource,
    int QueuedAhead,
    int QueueLength,
    int OtherAdmittedRuns,
    int CpuSlotsInUse,
    long MemoryMbInUse,
    DateTimeOffset QueuedAt,
    DateTimeOffset? AdmittedAt);

/// <summary>Outcome of one selected test project within a run.</summary>
/// <param name="ProjectPath">The test project.</param>
/// <param name="Status">How its run ended.</param>
/// <param name="TestCount">The tests it reported.</param>
/// <param name="NoTestsReason">Why an unfiltered run of the project reported no tests, when it reported none.</param>
/// <param name="TimedOut">The test that ran longer than the test timeout, when the project's run timed out.</param>
public sealed record TestRunProjectOutcome(
    string ProjectPath,
    ProjectRunStatus Status,
    int TestCount,
    string? NoTestsReason = null,
    TestTimeout? TimedOut = null)
{
    /// <summary>The exclusive test resources the project held and how long it waited, when it needed any.</summary>
    public TestProjectResources? Resources { get; init; }

    /// <summary>
    /// Set when a failed project's output looks like a port or container name conflict, with the evidence
    /// and a hint to declare the resource in <c>testResources</c>.
    /// </summary>
    public string? SuspectedResourceConflict { get; init; }
}

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
