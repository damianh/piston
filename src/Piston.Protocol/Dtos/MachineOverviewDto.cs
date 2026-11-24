// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

public sealed record MachineOverviewDto(
    DateTimeOffset CapturedAt,
    IReadOnlyList<MachineWorktreeDto> Worktrees,
    string? Error,
    bool OwnersAvailable = true);
