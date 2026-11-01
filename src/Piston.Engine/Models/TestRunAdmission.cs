// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>
/// Machine-wide execution admission for a run, shared across Piston daemons of the same user.
/// Counts of other runs are a snapshot from the last queue evaluation (while queued) or from admission.
/// </summary>
/// <param name="State">Queued or admitted.</param>
/// <param name="ReservedCpuSlots">CPU slots this run reserves (the daemon's test-process parallelism).</param>
/// <param name="ReservedMemoryMb">Memory this run reserves in MiB, when configured.</param>
/// <param name="BudgetCpuSlots">Machine CPU slot budget.</param>
/// <param name="BudgetMemoryMb">Machine memory budget in MiB; null when memory is not budgeted.</param>
/// <param name="BudgetSource">Budget file path, or <c>default</c>.</param>
/// <param name="QueuedAhead">Runs queued ahead of this one (0 once admitted).</param>
/// <param name="QueueLength">Runs waiting in the queue, including this one while queued.</param>
/// <param name="OtherAdmittedRuns">Other admitted runs holding reservations.</param>
/// <param name="CpuSlotsInUse">CPU slots reserved by other admitted runs.</param>
/// <param name="MemoryMbInUse">Memory reserved by other admitted runs, in MiB.</param>
/// <param name="QueuedAt">When this run joined the queue (or inherited its position).</param>
/// <param name="AdmittedAt">When the run was admitted.</param>
public sealed record TestRunAdmission(
    TestRunAdmissionState State,
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
    DateTimeOffset? AdmittedAt);
