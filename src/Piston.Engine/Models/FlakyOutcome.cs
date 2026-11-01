// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>One recorded outcome that a flaky label rests on.</summary>
/// <param name="RunId">The run that produced it.</param>
/// <param name="At">When the run finished.</param>
/// <param name="Status">Passed or Failed.</param>
/// <param name="Attempt">0 for a run's first attempt; n for its n-th retry.</param>
/// <param name="Worktree">The other worktree that recorded it; null for this worktree.</param>
public sealed record FlakyOutcome(string RunId, DateTimeOffset At, TestStatus Status, int Attempt, string? Worktree);
