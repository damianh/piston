// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>
/// Machine-wide execution admission of a run, shared by Piston daemons of the same user.
/// CPU and memory figures are admission reservations, not measured usage.
/// </summary>
public sealed record TestRunAdmissionDto(
    string State,
    int ReservedCpuSlots,
    int? ReservedMemoryMb,
    int BudgetCpuSlots,
    int? BudgetMemoryMb,
    string BudgetSource,
    int QueuedAhead,
    int QueueLength,
    int OtherAdmittedRuns,
    int CpuSlotsInUse,
    long MemoryMbInUse,
    DateTimeOffset QueuedAt,
    DateTimeOffset? AdmittedAt
);
