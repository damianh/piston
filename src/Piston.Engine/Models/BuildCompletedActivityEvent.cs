// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

public sealed record BuildCompletedActivityEvent(
    string? SolutionPath,
    bool Succeeded,
    double DurationMs,
    IReadOnlyList<string> Errors
) : EngineActivityEvent(SolutionPath)
{
    public int ErrorCount { get; init; } = Errors.Count;
}
