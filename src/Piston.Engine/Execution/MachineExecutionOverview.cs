// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>A machine-wide read-only snapshot of worktree owners, running reservations and queued runs.</summary>
public sealed record MachineExecutionOverview(
    DateTimeOffset CapturedAt,
    ExecutionBudget? Budget,
    IReadOnlyList<OwnershipRecord> Owners,
    IReadOnlyList<MachineExecutionEntry> Running,
    IReadOnlyList<MachineExecutionEntry> Queue,
    string? Error,
    bool CoordinationAvailable = true,
    bool OwnersAvailable = true)
{
    public int? CpuSlotsInUse => CoordinationAvailable ? Running.Sum(entry => entry.CpuSlots) : null;
    public long? MemoryMbInUse => CoordinationAvailable ? Running.Sum(entry => (long)(entry.MemoryMb ?? 0)) : null;
}
