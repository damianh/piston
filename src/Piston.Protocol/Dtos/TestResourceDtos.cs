// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>
/// Another test project run, possibly in another worktree, that holds (<c>Held</c>) or is queued ahead for
/// (<c>Queued</c>) a resource. <see cref="Resource"/> is a resource key or <c>containerSlots</c>.
/// </summary>
public sealed record TestResourceBlockerDto(
    string Resource,
    string Kind,
    int ProcessId,
    string? Worktree,
    string? Project,
    DateTimeOffset Since
);

/// <summary>Where a test project's run stands in the machine-wide test resource queue (<c>Waiting</c> or <c>Acquired</c>).</summary>
public sealed record TestResourceWaitDto(
    string State,
    IReadOnlyList<string> Keys,
    bool UsesContainers,
    IReadOnlyList<TestResourceBlockerDto> BlockedBy,
    int QueuedAhead,
    int ContainerSlotsInUse,
    int? ContainerSlotBudget,
    DateTimeOffset WaitingSince,
    DateTimeOffset? AcquiredAt,
    double? WaitedMs
);

/// <summary>
/// The exclusive resources a test project needed in a run, why, how long it waited, and any resource problems
/// (warnings such as a port already bound outside Piston, or a suspected port/container-name conflict in its output).
/// </summary>
public sealed record TestProjectResourcesDto(
    string ProjectPath,
    IReadOnlyList<string> Keys,
    bool UsesContainers,
    IReadOnlyList<string> Reasons,
    TestResourceWaitDto? Wait,
    IReadOnlyList<string> Warnings,
    string? SuspectedConflict
);
