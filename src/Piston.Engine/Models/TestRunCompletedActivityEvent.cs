// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

public sealed record TestRunCompletedActivityEvent(
    string? SolutionPath,
    int Passed,
    int Failed,
    int Skipped,
    double DurationMs,
    IReadOnlyList<TestFailure> Failures
) : EngineActivityEvent(SolutionPath);
