// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>Result of a test run, including authoritative suites and any runner-level error output.</summary>
public sealed record TestRunResult(
    IReadOnlyList<TestSuite> Suites,
    string? RunnerError,
    IReadOnlyList<string> CoverageReportPaths)
{
    /// <summary>Each project's own result, when the run tracked projects separately.</summary>
    public IReadOnlyList<ProjectTestResult> Projects { get; init; } = [];
}
