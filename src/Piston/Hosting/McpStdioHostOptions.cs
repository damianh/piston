// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine;

namespace Piston.Hosting;

/// <summary>Options for <see cref="McpStdioHost"/>.</summary>
public sealed class McpStdioHostOptions
{
    /// <summary>Resolved solution; null when <see cref="StartupError"/> is set.</summary>
    public string? SolutionPath { get; init; }

    /// <summary>Engine options; null when <see cref="StartupError"/> is set.</summary>
    public PistonOptions? EngineOptions { get; init; }

    /// <summary>
    /// Why the engine cannot start (no solution, no SDK, invalid config). The server still serves MCP so the
    /// agent sees this message from every tool instead of a dead server.
    /// </summary>
    public string? StartupError { get; init; }

    /// <summary>JSON-RPC input; defaults to stdin.</summary>
    public Stream? Input { get; init; }

    /// <summary>JSON-RPC output; defaults to stdout.</summary>
    public Stream? Output { get; init; }

    public TimeSpan? PresenceCheckInterval { get; init; }

    /// <summary>How long tools keep reporting the deletion before the process exits.</summary>
    public TimeSpan DeletionGracePeriod { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>On Unix, exit when the parent process (the agent) dies even if stdin stays open.</summary>
    public bool MonitorParentProcess { get; init; } = true;

    public bool Dashboard { get; init; }

    /// <summary>Diagnostic log sink; must not write to stdout.</summary>
    public Action<string> Log { get; init; } = message => Console.Error.WriteLine(message);
}
