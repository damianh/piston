// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using ModelContextProtocol;
using Piston.Engine.Execution;

namespace Piston.Mcp;

public enum EngineAvailabilityState
{
    Available,

    /// <summary>Another process owns the worktree; this server started no engine.</summary>
    OwnedElsewhere,

    /// <summary>The worktree or solution was deleted; the engine has stopped.</summary>
    WorktreeDeleted,

    /// <summary>The engine could not start (no SDK, no solution, coordination failure, ...).</summary>
    Faulted,

    /// <summary>The server is shutting down.</summary>
    Stopped,
}

/// <summary>
/// Gate checked by every MCP tool. When the engine is unavailable, tools fail with an actionable message
/// telling the agent to fall back to <c>dotnet test</c> instead of hanging or reporting stale results.
/// </summary>
public sealed class EngineAvailability
{
    public const string Fallback = "Use `dotnet test` instead.";

    private readonly Lock _lock = new();
    private EngineAvailabilityState _state = EngineAvailabilityState.Available;
    private string? _reason;

    public EngineAvailabilityState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>Message returned by tools while unavailable; null when available.</summary>
    public string? Reason
    {
        get
        {
            lock (_lock)
            {
                return _reason;
            }
        }
    }

    public void SetOwnedElsewhere(WorktreeIdentity worktree, ExecutionOwnerInfo? owner)
    {
        var who = owner is null
            ? "another process (details unavailable)"
            : $"another process (PID {owner.ProcessId}, {owner.Host ?? "daemon"}, solution '{owner.SolutionPath}', since {owner.AcquiredAt:O})";
        Set(EngineAvailabilityState.OwnedElsewhere,
            $"Piston owned by {who} for worktree '{worktree.RootPath}'. " +
            $"This MCP server started no engine and will not run tests here. {Fallback}");
    }

    public void SetWorktreeDeleted(string path) =>
        Set(EngineAvailabilityState.WorktreeDeleted,
            $"Piston stopped: the worktree/solution '{path}' no longer exists. {Fallback}");

    public void SetFaulted(string message) =>
        Set(EngineAvailabilityState.Faulted, $"Piston could not start: {message} {Fallback}");

    /// <summary>Marks the server as shutting down unless a more specific reason is already set.</summary>
    public void SetStopped()
    {
        lock (_lock)
        {
            if (_state == EngineAvailabilityState.Available)
            {
                _state = EngineAvailabilityState.Stopped;
                _reason = $"Piston is shutting down. {Fallback}";
            }
        }
    }

    /// <exception cref="McpException">The engine is unavailable; the message explains why.</exception>
    public void ThrowIfUnavailable()
    {
        lock (_lock)
        {
            if (_state != EngineAvailabilityState.Available)
            {
                throw new McpException(_reason!);
            }
        }
    }

    private void Set(EngineAvailabilityState state, string reason)
    {
        lock (_lock)
        {
            _state = state;
            _reason = reason;
        }
    }
}
