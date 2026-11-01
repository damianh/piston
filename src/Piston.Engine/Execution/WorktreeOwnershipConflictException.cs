// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>Another live process already owns execution for the worktree.</summary>
public sealed class WorktreeOwnershipConflictException(WorktreeIdentity worktree, ExecutionOwnerInfo? owner) : ExecutionCoordinationException(Describe(worktree, owner))
{
    public WorktreeIdentity Worktree { get; } = worktree;

    /// <summary>Owner details recorded by the holder; null when they could not be read.</summary>
    public ExecutionOwnerInfo? Owner { get; } = owner;

    private static string Describe(WorktreeIdentity worktree, ExecutionOwnerInfo? owner) =>
        owner is null
            ? $"Worktree '{worktree.RootPath}' is already owned by another running Piston daemon."
            : $"Worktree '{worktree.RootPath}' is already owned by Piston {owner.Host ?? "daemon"} process {owner.ProcessId} " +
              $"(solution '{owner.SolutionPath}', since {owner.AcquiredAt:O}).";
}
