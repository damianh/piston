// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

public sealed record MachineBudgetDto(
    int CpuSlots,
    int? MemoryMb,
    int? CpuSlotsInUse,
    long? MemoryMbInUse,
    int? RunningCount,
    int? QueuedCount,
    string Source);
