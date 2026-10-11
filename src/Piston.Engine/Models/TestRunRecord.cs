namespace Piston.Engine.Models;

/// <summary>What caused a test run to be requested.</summary>
public enum TestRunTrigger
{
    /// <summary>The initial run issued when the engine starts.</summary>
    Startup,

    /// <summary>An explicit full run request (MCP <c>RunTests</c>, protocol force-run).</summary>
    Forced,

    /// <summary>A file-watcher change batch (normally a selective run).</summary>
    FileChange,
}

/// <summary>Lifecycle state of a single test run.</summary>
public enum TestRunStatus
{
    /// <summary>Requested but waiting for the previous pipeline to release the run lock.</summary>
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
/// <param name="Algorithm">Fingerprint algorithm identifier, e.g. <c>piston-source-v1</c>.</param>
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
/// <param name="SdkVersion">Output of <c>dotnet --version</c> in the daemon's working directory, when available.</param>
/// <param name="Runtime">Runtime hosting the Piston engine.</param>
/// <param name="OperatingSystem">Host operating system description.</param>
/// <param name="WorkingDirectory">Working directory of the build/test processes (determines <c>global.json</c> resolution).</param>
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
    string WorkingDirectory);

/// <summary>Outcome of one selected test project within a run.</summary>
public sealed record TestRunProjectOutcome(string ProjectPath, ProjectRunStatus Status, int TestCount);

/// <summary>
/// Provenance and results of exactly one test run. Unlike the aggregate
/// <see cref="PistonState.TestSuites"/>, <see cref="Suites"/> contains only tests executed by this run.
/// </summary>
public sealed record TestRunRecord
{
    public required string RunId { get; init; }

    /// <summary>Identifies the engine instance; <see cref="Sequence"/> is only ordered within one instance.</summary>
    public required string EngineInstanceId { get; init; }

    /// <summary>Monotonically increasing request order within an engine instance.</summary>
    public required long Sequence { get; init; }

    public required TestRunTrigger Trigger { get; init; }
    public TestRunStatus Status { get; init; } = TestRunStatus.Queued;

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
    public IReadOnlyList<TestRunProjectOutcome> Projects { get; init; } = [];

    /// <summary>Suites produced by this run only (never merged with earlier runs).</summary>
    public IReadOnlyList<TestSuite> Suites { get; init; } = [];

    /// <summary>Fully-qualified names reported more than once by this run.</summary>
    public IReadOnlyList<string> DuplicateTestIds { get; init; } = [];

    public string? RunnerError { get; init; }

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
        && IsFullScope
        && Freshness == ResultFreshness.Current
        && DuplicateTestIds.Count == 0;

    public IEnumerable<TestResult> Tests => Suites.SelectMany(s => s.Tests);
}
