// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

public sealed record ProjectTestResult(
    string ProjectPath,
    IReadOnlyList<TestSuite> Suites,
    string? RunnerError,
    IReadOnlyList<string> CoverageReportPaths,
    bool Crashed)
{
    /// <summary>The log of the process that produced the result, when the runner kept one.</summary>
    public ExecutionLog? Log { get; init; }

    /// <summary>Set when a test ran longer than the test timeout and the project's run was cancelled.</summary>
    public TestTimeout? TimedOut { get; init; }

    /// <summary>The exclusive test resources the run held, when it needed any.</summary>
    public TestProjectResources? Resources { get; init; }
}
