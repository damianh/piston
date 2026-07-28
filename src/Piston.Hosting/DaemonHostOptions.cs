namespace Piston.Hosting;

/// <summary>
/// Options for hosting a Piston daemon in-process via <see cref="DaemonHost"/>.
/// </summary>
public sealed class DaemonHostOptions
{
    /// <summary>Absolute path to the .sln/.slnx/.slnf file to watch.</summary>
    public required string SolutionPath { get; init; }

    /// <summary>Engine options (debounce, filter, coverage, parallelism, ...).</summary>
    public required Piston.Engine.PistonOptions EngineOptions { get; init; }

    /// <summary>Named pipe name for local IPC clients (CLI, IDE extensions).</summary>
    public required string PipeName { get; init; }

    /// <summary>Port for the web dashboard (WebSocket + static Blazor WASM assets).</summary>
    public int WebPort { get; init; } = 5199;

    /// <summary>Optional port for the MCP server. Null disables MCP.</summary>
    public int? McpPort { get; init; }

    /// <summary>
    /// Diagnostic log sink. Defaults to writing to <see cref="Console.Error"/>.
    /// </summary>
    public Action<string> Log { get; init; } = message => Console.Error.WriteLine(message);
}
