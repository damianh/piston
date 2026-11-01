// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Outcome of one selected test project within a run.</summary>
/// <param name="ProjectPath">The test project.</param>
/// <param name="Status">How its run ended.</param>
/// <param name="TestCount">The tests it reported.</param>
/// <param name="NoTestsReason">Why an unfiltered run of the project reported no tests, when it reported none.</param>
/// <param name="TimedOut">The test that ran longer than the test timeout, when the project's run timed out.</param>
public sealed record TestRunProjectOutcome(
    string ProjectPath,
    ProjectRunStatus Status,
    int TestCount,
    string? NoTestsReason = null,
    TestTimeout? TimedOut = null)
{
    public string ProjectId { get; init; } = ProjectPath;

    /// <summary>The exclusive test resources the project held and how long it waited, when it needed any.</summary>
    public TestProjectResources? Resources { get; init; }

    /// <summary>
    /// Set when a failed project's output looks like a port or container name conflict, with the evidence
    /// and a hint to declare the resource in <c>testResources</c>.
    /// </summary>
    public string? SuspectedResourceConflict { get; init; }
}
