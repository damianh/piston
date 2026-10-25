// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;

namespace Piston.Engine.Models;

public enum TestResourceWaitState
{
    /// <summary>Queued behind runs that hold or are ahead for a needed resource.</summary>
    Waiting,

    /// <summary>All needed resources are held by this run.</summary>
    Acquired,
}

public enum TestResourceBlockerKind
{
    /// <summary>A running test project holds the resource.</summary>
    Held,

    /// <summary>A test project queued earlier needs the resource and goes first.</summary>
    Queued,
}

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

/// <summary>Where a test project's run stands in the machine-wide test resource queue.</summary>
/// <param name="State">Waiting or acquired.</param>
/// <param name="Keys">The exclusive resources the run needs.</param>
/// <param name="UsesContainers">Whether the run needs a container slot.</param>
/// <param name="BlockedBy">Runs holding or queued ahead for a needed resource; empty once acquired.</param>
/// <param name="QueuedAhead">Earlier queued runs that conflict with this one.</param>
/// <param name="ContainerSlotsInUse">Container slots held by running test projects machine-wide.</param>
/// <param name="ContainerSlotBudget">The machine's <c>containerSlots</c>, or null when unlimited.</param>
/// <param name="WaitingSince">When the run asked for its resources.</param>
/// <param name="AcquiredAt">When it got them, or null while waiting.</param>
public sealed record TestResourceWait(
    TestResourceWaitState State,
    IReadOnlyList<string> Keys,
    bool UsesContainers,
    IReadOnlyList<TestResourceBlocker> BlockedBy,
    int QueuedAhead,
    int ContainerSlotsInUse,
    int? ContainerSlotBudget,
    DateTimeOffset WaitingSince,
    DateTimeOffset? AcquiredAt)
{
    /// <summary>How long the run waited, once acquired.</summary>
    public TimeSpan? Waited => AcquiredAt - WaitingSince;

    internal bool SameAs(TestResourceWait? other) =>
        other is not null
        && State == other.State
        && QueuedAhead == other.QueuedAhead
        && ContainerSlotsInUse == other.ContainerSlotsInUse
        && ContainerSlotBudget == other.ContainerSlotBudget
        && BlockedBy.SequenceEqual(other.BlockedBy);
}

/// <summary>The shared resources of one test project's run and how they were obtained.</summary>
/// <param name="Needs">What the run needed and why.</param>
/// <param name="Wait">The final queue status; null when the run needed no coordination.</param>
/// <param name="Warnings">Problems noticed, e.g. a declared port already bound by a non-Piston process.</param>
public sealed record TestProjectResources(
    TestResourceNeeds Needs,
    TestResourceWait? Wait,
    IReadOnlyList<string> Warnings);
