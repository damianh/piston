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
}

