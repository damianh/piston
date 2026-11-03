// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>A unit measured by a coverage run.</summary>
/// <param name="ProjectPath">The test project.</param>
/// <param name="UnitKey">The unit within the project: <c>run</c>, a class name or a test name.</param>
/// <param name="ReportPaths">The Cobertura reports produced for it.</param>
/// <param name="TestFqns">The tests that ran.</param>
/// <param name="Merge">True when only some of the unit's tests ran, so the recording adds to what is stored.</param>
/// <param name="MethodsComplete">See <see cref="Impact.ImpactRecording.MethodsComplete"/>.</param>
internal sealed record CoverageRunUnit(
    string ProjectPath,
    string UnitKey,
    IReadOnlyList<string> ReportPaths,
    IReadOnlyList<string> TestFqns,
    bool Merge,
    bool MethodsComplete = false)
{
    /// <summary>Framework-qualified results represented by a complete finer-grained measurement.</summary>
    public IReadOnlyList<string>? TestResultKeys { get; init; }
}
