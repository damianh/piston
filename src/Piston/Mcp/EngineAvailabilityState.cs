// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

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
