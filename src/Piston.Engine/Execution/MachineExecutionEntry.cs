// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>One running or queued machine-wide execution reservation.</summary>
public sealed record MachineExecutionEntry(
    long Sequence,
    string RunId,
    int ProcessId,
    int CpuSlots,
    int? MemoryMb,
    string? WorktreeKey,
    int? QueuePosition);
