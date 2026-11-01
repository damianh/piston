// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>One outcome counted as flakiness evidence.</summary>
/// <param name="Attempt">0 for the run's first attempt; n for its n-th retry.</param>
/// <param name="Worktree">The other worktree that recorded it; null for this worktree.</param>
public sealed record FlakyOutcomeDto(string RunId, DateTimeOffset At, string Status, int Attempt, string? Worktree);
