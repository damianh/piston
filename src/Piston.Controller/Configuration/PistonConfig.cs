namespace Piston.Controller.Configuration;

/// <summary>
/// Shape of the optional <c>.piston.json</c> config file.
/// All fields are optional; missing values fall back to CLI args or built-in defaults.
/// </summary>
internal sealed class PistonConfig
{
    /// <summary>Path to the solution file. Relative paths are resolved from the config file location.</summary>
    public string? Solution { get; set; }

    /// <summary>Debounce interval in milliseconds (default 300).</summary>
    public int? DebounceMs { get; set; }

    /// <summary>Default test filter substring/regex applied on startup.</summary>
    public string? TestFilter { get; set; }

    /// <summary>
    /// When true, enables code coverage collection during test runs.
    /// Corresponds to the <c>--coverage</c> CLI flag.
    /// </summary>
    public bool? CoverageEnabled { get; set; }
}
