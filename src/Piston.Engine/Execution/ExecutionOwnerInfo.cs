// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>Details recorded by a worktree owner for diagnostics when another daemon is refused.</summary>
/// <param name="Host">How the owner was started (<c>daemon</c>, <c>mcp</c>, ...); null when not recorded.</param>
/// <param name="DashboardUrl">The owner's local dashboard URL, when it has one.</param>
public sealed record ExecutionOwnerInfo(
    int ProcessId, string SolutionPath, string WorktreePath, DateTimeOffset AcquiredAt, string? Host = null,
    string? DashboardUrl = null);
