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
    /// "test" (default), "class" or "off". Per-test backfill is precise but expensive.
    /// </summary>
    public string? ImpactBackfill { get; set; }

    /// <summary>
    /// How long a test runs before it is reported as possibly hanging: seconds (<c>90</c>) or a number with a
    /// unit (<c>500ms</c>, <c>30s</c>, <c>5m</c>, <c>1.5h</c>); <c>0</c> or <c>off</c> turns it off. Default 60 s.
    /// </summary>
    public string? HangThreshold { get; set; }

    /// <summary>
    /// Per-test limit, in the same format as <see cref="HangThreshold"/>: a test that runs longer fails as timed
    /// out and its project's run is cancelled. Off by default.
    /// </summary>
    public string? TestTimeout { get; set; }

    /// <summary>
    /// Keep per-test run history outside the checkout and classify failures as new, pre-existing or flaky.
    /// Default true.
    /// </summary>
    public bool? History { get; set; }

    /// <summary>Save the latest test results between daemon restarts. Default true.</summary>
    public bool? PersistResults { get; set; }

    /// <summary>
    /// How many times a run re-runs its failed tests, without rebuilding, to gather flakiness evidence: 0 to 3.
    /// A test that passes on a retry counts as passed and is reported as passed on retry. Default 0 (off).
    /// </summary>
    public int? RetryFailures { get; set; }

    /// <summary>
    /// Keep Microsoft.Testing.Platform test apps running in server mode between runs, so later runs skip
    /// host start-up. Default false.
    /// </summary>
    public bool? WarmTestHosts { get; set; }

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

    /// <summary>
    /// Overrides of the shared resources Piston infers for test projects, applied in order to the projects
    /// each entry's glob matches.
    /// </summary>
    public List<PistonTestResourceConfig>? TestResources { get; set; }
}

/// <summary>A <c>testResources</c> entry of <c>.piston.json</c>.</summary>
public sealed class PistonTestResourceConfig
{
    /// <summary>Property names an entry may use; anything else is rejected.</summary>
    public static readonly IReadOnlyList<string> PropertyNames = ["projects", "exclusive", "containers", "isolation"];

    /// <summary>Glob over project paths relative to the solution directory, e.g. <c>tests/**/*.IntegrationTests.csproj</c>.</summary>
    public string? Projects { get; set; }

    /// <summary>Exclusive resources: <c>port:5432</c>, <c>container:orders-db</c>, <c>resource:shared-queue</c>.</summary>
    public List<string>? Exclusive { get; set; }

    /// <summary>Overrides whether the projects use containers (counted against the machine's <c>containerSlots</c>).</summary>
    public bool? Containers { get; set; }

    /// <summary>"repository" (one run per repository at a time) or "none".</summary>
    public string? Isolation { get; set; }
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
