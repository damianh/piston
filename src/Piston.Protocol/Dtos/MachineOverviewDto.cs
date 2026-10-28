// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

public sealed record MachineOverviewDto(
    DateTimeOffset CapturedAt,
    MachineBudgetDto? Budget,
    IReadOnlyList<MachineWorktreeDto> Worktrees,
    IReadOnlyList<MachineExecutionEntryDto> Running,
    IReadOnlyList<MachineExecutionEntryDto> Queue,
    string? Error,
    bool CoordinationAvailable = true,
    bool OwnersAvailable = true);

public sealed record MachineBudgetDto(
    int CpuSlots,
    int? MemoryMb,
    int? CpuSlotsInUse,
    long? MemoryMbInUse,
    int? RunningCount,
    int? QueuedCount,
    string Source);

public sealed record MachineWorktreeDto(
    string Key,
    string Liveness,
    int? ProcessId,
    string? SolutionPath,
    string? WorktreePath,
    DateTimeOffset? AcquiredAt,
    string? Host,
    string? DashboardUrl,
    bool IsCurrent,
    string State,
    int? QueuePosition);

public sealed record MachineExecutionEntryDto(
    long Sequence,
    string RunId,
    int ProcessId,
    int CpuSlots,
    int? MemoryMb,
    string? WorktreeKey,
    string? WorktreePath,
    int? QueuePosition);
