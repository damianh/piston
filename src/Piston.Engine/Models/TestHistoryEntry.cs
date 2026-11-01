// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>One recorded result of a test.</summary>
/// <param name="RunId">The run that produced it.</param>
/// <param name="At">When the run finished.</param>
/// <param name="Status">Passed, Failed or Skipped.</param>
/// <param name="DurationMs">Duration in milliseconds.</param>
/// <param name="Source">The run's source fingerprint when its results were current; null otherwise.</param>
/// <param name="Attempt">0 for the run's first attempt; n for its n-th retry of the test.</param>
public sealed record TestHistoryEntry(string RunId, DateTimeOffset At, TestStatus Status, double DurationMs, string? Source, int Attempt = 0);
