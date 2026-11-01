// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Impact;

/// <summary>A source file as a unit's tests executed it.</summary>
/// <param name="FilePath">Absolute path of the source file.</param>
/// <param name="Spans">Executed method line ranges.</param>
/// <param name="Content">The file's content when it was measured; changes are found by diffing against it.</param>
/// <param name="Branches">Per-line branch outcomes, or null when branch evidence was not recorded.</param>
internal sealed record ImpactFile(
    string FilePath,
    IReadOnlyList<LineSpan> Spans,
    string Content,
    IReadOnlyList<BranchLine>? Branches = null);
