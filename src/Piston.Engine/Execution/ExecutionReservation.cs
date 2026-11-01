// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>What one admitted run of a daemon reserves from the <see cref="ExecutionBudget"/>.</summary>
/// <param name="CpuSlots">The daemon's effective test-process parallelism.</param>
/// <param name="MemoryMb">Configured memory reservation in MiB, if any.</param>
public sealed record ExecutionReservation(int CpuSlots, int? MemoryMb)
{
    /// <summary>Throws when this reservation could never be admitted under <paramref name="budget"/>.</summary>
    public void EnsureAdmissible(ExecutionBudget budget)
    {
        if (CpuSlots <= 0)
        {
            throw new ExecutionCoordinationException($"The CPU slot reservation must be positive; got {CpuSlots}.");
        }

        if (MemoryMb is <= 0)
        {
            throw new ExecutionCoordinationException($"The memory reservation must be positive; got {MemoryMb}.");
        }

        if (CpuSlots > budget.CpuSlots)
        {
            throw new ExecutionCoordinationException(
                $"This daemon reserves {CpuSlots} CPU slot(s) per run (its test-process parallelism), but the machine " +
                $"execution budget ({budget.Source}) allows {budget.CpuSlots}; it could never be admitted. " +
                "Lower 'parallelism' or raise 'cpuSlots' in the budget.");
        }

        if (budget.MemoryMb is { } memoryBudget)
        {
            if (MemoryMb is null)
            {
                throw new ExecutionCoordinationException(
                    $"The machine execution budget ({budget.Source}) limits memory to {memoryBudget} MiB, but this " +
                    "daemon declares no memory reservation. Set 'execution.memoryMb' in .piston.json.");
            }

            if (MemoryMb > memoryBudget)
            {
                throw new ExecutionCoordinationException(
                    $"This daemon reserves {MemoryMb} MiB per run, but the machine execution budget ({budget.Source}) " +
                    $"allows {memoryBudget} MiB; it could never be admitted.");
            }
        }
    }
}
