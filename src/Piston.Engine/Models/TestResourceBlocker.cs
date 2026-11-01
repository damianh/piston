// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Another test project run (possibly in another worktree) that a project is waiting for.</summary>
/// <param name="Resource">The contended resource key, or <see cref="ContainerSlots"/>.</param>
/// <param name="Kind">Whether the other run holds the resource or is ahead in the queue.</param>
/// <param name="ProcessId">The Piston process of the other run.</param>
/// <param name="Worktree">The other run's worktree root, when known.</param>
/// <param name="Project">The other run's test project, when known.</param>
/// <param name="Since">When the other run queued or acquired.</param>
public sealed record TestResourceBlocker(
    string Resource,
    TestResourceBlockerKind Kind,
    int ProcessId,
    string? Worktree,
    string? Project,
    DateTimeOffset Since)
{
    /// <summary>The <see cref="Resource"/> of blockers that occupy the machine's container slots.</summary>
    public const string ContainerSlots = "containerSlots";
}
