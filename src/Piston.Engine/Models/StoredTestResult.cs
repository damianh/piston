// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>One test's result in a stored run. Messages and stack traces are kept for failures only, clipped.</summary>
/// <param name="Attempts">Earlier attempts in the run, oldest first, when the test was retried; null otherwise.</param>
public sealed record StoredTestResult(
    string Project,
    string Test,
    TestStatus Status,
    double DurationMs,
    string? ErrorMessage = null,
    string? StackTrace = null,
    IReadOnlyList<StoredAttempt>? Attempts = null)
{
    public string ProjectId { get; init; } = Project;
}
