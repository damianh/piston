// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Impact;

/// <summary>Why a test was selected.</summary>
/// <param name="FilePath">The changed file.</param>
/// <param name="ChangedLines">The changed lines, numbered as in the recorded snapshot.</param>
/// <param name="ExecutedSpan">
/// The executed method range the change overlaps, or null when the change lies outside every range the
/// tests executed (or the file was deleted) and every test that executed the file is selected.
/// </param>
/// <param name="Granularity">How precisely the evidence attributes the file to the test.</param>
/// <param name="ProjectPath">The test project.</param>
/// <param name="Branches">Known branch counts on changed lines, or null when unavailable.</param>
internal sealed record ImpactReason(
    string FilePath,
    LineSpan ChangedLines,
    LineSpan? ExecutedSpan,
    ImpactGranularity Granularity,
    string ProjectPath,
    string? Member = null,
    IReadOnlyList<BranchLine>? Branches = null);
