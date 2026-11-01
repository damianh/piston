// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Coverage;

/// <summary>A test the impact map credits with executing a source file.</summary>
/// <param name="TestFqn">Fully-qualified test name.</param>
/// <param name="ProjectPath">The test project.</param>
/// <param name="Granularity">
/// How the evidence was measured: <c>run</c> (every test in the project's run together), <c>class</c> (the test's
/// class run alone) or <c>test</c> (the test alone). Run- and class-level evidence over-attributes: it does not mean
/// this test executed the file.
/// </param>
/// <param name="ExecutedLines">The executed method ranges, numbered as in the content that was measured.</param>
/// <param name="IsCurrent">
/// The file is unchanged since it was measured and the measured code has not changed since, so the ranges line up
/// with the file as it is now.
/// </param>
/// <param name="Branches">Per-line branch outcomes, null if branch data was not recorded or the source is stale.</param>
public sealed record CoveringTest(
    string TestFqn,
    string ProjectPath,
    string Granularity,
    IReadOnlyList<CoveredLineRange> ExecutedLines,
    bool IsCurrent,
    IReadOnlyList<BranchEvidence>? Branches = null);
