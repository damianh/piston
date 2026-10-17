// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

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

    /// <summary>MCP port the CLI uses when neither <c>--mcp-port</c> nor <c>mcpPort</c> is set.</summary>
    public const int DefaultMcpPort = 5200;

    /// <summary>
    /// Port for the MCP server. The CLI always sets it (defaulting to <see cref="DefaultMcpPort"/>);
    /// null, used only by in-process hosts, disables MCP.
    /// </summary>
    public int? McpPort { get; init; }

    /// <summary>How often to check whether the worktree still exists; null uses the default.</summary>
    public TimeSpan? PresenceCheckInterval { get; init; }

    /// <summary>
    /// Diagnostic log sink. Defaults to writing to <see cref="Console.Error"/>.
    /// </summary>
    public Action<string> Log { get; init; } = message => Console.Error.WriteLine(message);
}
