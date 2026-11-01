// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Why a test was selected: an edit touched a file it executed.</summary>
/// <param name="FilePath">The changed file.</param>
/// <param name="ChangedLines">The lines the edit touched in the measured version, e.g. <c>12-14</c>.</param>
/// <param name="ExecutedLines">
/// The executed range the edit overlapped, or null when the edit fell outside every executed range and the test
/// was selected because it executed the file at all.
/// </param>
/// <param name="Granularity">
/// What the evidence measured: <c>run</c> (every test in the project's run together), <c>class</c> or
/// <c>test</c>. Run- and class-level evidence over-selects; it does not mean this test executed the lines.
/// </param>
/// <param name="ProjectPath">The test project the evidence came from.</param>
public sealed record TestSelectionReason(
    string FilePath,
    string ChangedLines,
    string? ExecutedLines,
    string Granularity,
    string ProjectPath,
    string? Member = null,
    IReadOnlyList<BranchEvidence>? Branches = null);
