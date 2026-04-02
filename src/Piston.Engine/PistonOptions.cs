namespace Piston.Engine;

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
}
