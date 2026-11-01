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
