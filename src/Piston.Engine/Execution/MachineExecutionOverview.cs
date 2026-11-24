// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>A machine-wide read-only snapshot of worktree owners.</summary>
public sealed record MachineExecutionOverview(
    DateTimeOffset CapturedAt,
    IReadOnlyList<OwnershipRecord> Owners,
    string? Error,
    bool OwnersAvailable = true);
