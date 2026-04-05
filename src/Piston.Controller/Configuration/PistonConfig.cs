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

    /// <summary>
    /// Maximum number of concurrent <c>dotnet test</c> processes.
    /// 0 means auto-detect. Corresponds to the <c>--parallelism</c> CLI flag.
    /// </summary>
    public int? Parallelism { get; set; }

    /// <summary>
    /// Number of runs after which a pool slot logs a recycling warning.
    /// Default is 50.
    /// </summary>
    public int? ProcessRecycleAfter { get; set; }

    /// <summary>
    /// Override the named pipe name used in headless mode.
    /// When omitted, the pipe name is auto-generated from the solution path.
    /// Corresponds to the <c>--pipe-name</c> CLI flag.
    /// </summary>
    public string? PipeName { get; set; }

    /// <summary>
    /// When true, defaults to headless mode (no TUI).
    /// Corresponds to the <c>--headless</c> CLI flag.
    /// </summary>
    public bool? Headless { get; set; }

    /// <summary>
    /// Test execution mode. Accepted values: "Auto", "Process", "InProcess".
    /// Stored as a string to avoid a type dependency on <c>Piston.Engine</c>.
    /// </summary>
    public string? TestExecutionMode { get; set; }
}
