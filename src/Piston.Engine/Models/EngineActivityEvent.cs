// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Engine-owned activity, independent of client wire formats.</summary>
public abstract record EngineActivityEvent(string? SolutionPath)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SolutionLoadedActivityEvent(
    string LoadedSolutionPath,
    int ProjectCount
) : EngineActivityEvent(LoadedSolutionPath);

public sealed record FileChangesDetectedActivityEvent(
    string? SolutionPath,
    IReadOnlyList<string> ChangedFiles
) : EngineActivityEvent(SolutionPath);

public sealed record BuildCompletedActivityEvent(
    string? SolutionPath,
    bool Succeeded,
    double DurationMs,
    IReadOnlyList<string> Errors
) : EngineActivityEvent(SolutionPath)
{
    public int ErrorCount { get; init; } = Errors.Count;
}

public sealed record TestFailure(
    string FullyQualifiedName,
    string? Message
);

public sealed record TestRunCompletedActivityEvent(
    string? SolutionPath,
    int Passed,
    int Failed,
    int Skipped,
    double DurationMs,
    IReadOnlyList<TestFailure> Failures
) : EngineActivityEvent(SolutionPath);
