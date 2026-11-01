// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Impact;

/// <summary>A test the impact map credits with executing a file.</summary>
/// <param name="Spans">The executed method ranges, numbered as in the snapshot the unit was measured against.</param>
/// <param name="SnapshotHash">Hash of the file's content when measured.</param>
/// <param name="IsUnitCurrent">False when the unit's code changed and it awaits re-measurement.</param>
/// <param name="IsContentCurrent">False when the file's current code differs from the measured snapshot.</param>
/// <param name="Branches">Per-line branch counts, remapped to current content when it is code-current.</param>
internal sealed record ImpactExecutor(
    string ProjectPath,
    string TestFqn,
    ImpactGranularity Granularity,
    IReadOnlyList<LineSpan> Spans,
    string SnapshotHash,
    bool IsUnitCurrent,
    bool IsContentCurrent,
    IReadOnlyList<BranchLine>? Branches = null);
