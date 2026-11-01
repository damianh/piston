// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>One recorded result of a test.</summary>
/// <param name="Source">The run's source fingerprint when its results were current; otherwise null.</param>
/// <param name="Attempt">0 for the run's first attempt; n for its n-th retry of the test.</param>
public sealed record TestHistoryEntryDto(string RunId, DateTimeOffset At, string Status, double DurationMs, string? Source, int Attempt = 0);
