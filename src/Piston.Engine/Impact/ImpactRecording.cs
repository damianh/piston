// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Impact;

/// <summary>What one measured unit (a project's run, a test class or a test) executed.</summary>
/// <param name="ProjectPath">The test project the unit belongs to.</param>
/// <param name="Granularity">How the unit was measured.</param>
/// <param name="UnitKey">Identifies the unit within the project: <c>run</c>, a class name or a test name.</param>
/// <param name="RunId">The coverage run that measured it.</param>
/// <param name="TestFqns">The tests credited with the unit's files.</param>
/// <param name="Files">The files executed, with their content when measured.</param>
/// <param name="Merge">
/// True when only some of the unit's tests ran (a selective run): tests and spans are added to what is
/// recorded rather than replacing it. False replaces the unit and marks it current.
/// </param>
/// <param name="MethodsComplete">
/// For a merge: every test case of each method in <paramref name="TestFqns"/> ran (selective runs filter by
/// method). Recorded tests of those methods that did not run no longer exist, such as theory cases whose
/// display names change between runs, and are removed.
/// </param>
internal sealed record ImpactRecording(
    string ProjectPath,
    ImpactGranularity Granularity,
    string UnitKey,
    long RunId,
    IReadOnlyList<string> TestFqns,
    IReadOnlyList<ImpactFile> Files,
    bool Merge,
    bool MethodsComplete = false);
