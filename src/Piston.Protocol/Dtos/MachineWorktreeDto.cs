// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

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
