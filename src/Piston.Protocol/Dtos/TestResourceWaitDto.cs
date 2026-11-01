// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

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
