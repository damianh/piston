// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Hosting.Configuration;

/// <summary>
/// Shape of the optional <c>.piston.json</c> config file.
/// All fields are optional; missing values fall back to CLI args or built-in defaults.
/// </summary>
public sealed class PistonConfig
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
    /// With coverage enabled, how finely to measure which tests execute which lines while idle:
    /// "class" (default), "test" or "off".
    /// </summary>
    public string? ImpactBackfill { get; set; }

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
    /// When true, use stdin/stdout for JSON-RPC transport instead of a named pipe.
    /// Corresponds to the <c>--stdio</c> CLI flag on the <c>daemon</c> subcommand.
    /// </summary>
    public bool? Stdio { get; set; }

    /// <summary>
    /// Port for the daemon's MCP server (default 5200). Corresponds to the <c>--mcp-port</c> CLI flag.
    /// </summary>
    public int? McpPort { get; set; }

    /// <summary>
    /// Test execution mode. Accepted values: "Auto", "Process", "InProcess".
    /// Stored as a string to avoid a type dependency on <c>Piston.Engine</c>.
    /// </summary>
    public string? TestExecutionMode { get; set; }

    /// <summary>Machine-wide execution admission settings for this daemon.</summary>
    public PistonExecutionConfig? Execution { get; set; }
}

/// <summary>The <c>execution</c> section of <c>.piston.json</c>.</summary>
public sealed class PistonExecutionConfig
{
    /// <summary>
    /// Memory in MiB each run reserves from the machine execution budget. Must be positive.
    /// Required when the machine budget limits memory. It is an admission reservation, not a cap.
    /// </summary>
    public int? MemoryMb { get; set; }
}
