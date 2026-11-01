// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

public sealed record MachineExecutionEntryDto(
    long Sequence,
    string RunId,
    int ProcessId,
    int CpuSlots,
    int? MemoryMb,
    string? WorktreeKey,
    string? WorktreePath,
    int? QueuePosition);
