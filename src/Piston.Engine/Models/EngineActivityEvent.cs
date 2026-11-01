// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Engine-owned activity, independent of client wire formats.</summary>
public abstract record EngineActivityEvent(string? SolutionPath)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
