namespace Piston.Engine;

/// <summary>
/// Resolved runtime options — merged from CLI args, .piston.json, and defaults.
/// Passed into services at startup.
/// </summary>
public sealed class PistonOptions
{
    /// <summary>Absolute path to the .sln / .slnx file to watch.</summary>
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
}
