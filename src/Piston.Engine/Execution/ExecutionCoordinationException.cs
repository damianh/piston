// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>
/// Machine-wide execution coordination is unavailable, misconfigured or cannot admit work.
/// Piston reports this explicitly instead of running without ownership or budgeting.
/// </summary>
public class ExecutionCoordinationException : Exception
{
    public ExecutionCoordinationException(string message) : base(message) { }

    public ExecutionCoordinationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Another live process already owns execution for the worktree.</summary>
public sealed class WorktreeOwnershipConflictException : ExecutionCoordinationException
{
    public WorktreeOwnershipConflictException(WorktreeIdentity worktree, ExecutionOwnerInfo? owner)
        : base(Describe(worktree, owner))
    {
        Worktree = worktree;
        Owner = owner;
    }

    public WorktreeIdentity Worktree { get; }

    /// <summary>Owner details recorded by the holder; null when they could not be read.</summary>
    public ExecutionOwnerInfo? Owner { get; }

    private static string Describe(WorktreeIdentity worktree, ExecutionOwnerInfo? owner) =>
        owner is null
            ? $"Worktree '{worktree.RootPath}' is already owned by another running Piston daemon."
            : $"Worktree '{worktree.RootPath}' is already owned by Piston daemon process {owner.ProcessId} " +
              $"(solution '{owner.SolutionPath}', since {owner.AcquiredAt:O}).";
}

/// <summary>
/// Admission was cancelled while queued. <see cref="QueueSequence"/> lets a superseding run in the
/// same daemon keep the queue position instead of re-joining at the back; null when the run never
/// joined the queue.
/// </summary>
public sealed class ExecutionAdmissionCancelledException(long? queueSequence, CancellationToken token)
    : OperationCanceledException("Cancelled while queued for machine-wide execution admission.", token)
{
    public long? QueueSequence { get; } = queueSequence;
}
