// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;

namespace Piston.Mcp.Tools;

/// <summary>The verdict for one changed line.</summary>
/// <param name="Line">1-based line number in the current file.</param>
/// <param name="Coverage">The line's aggregate coverage, when the file was measured as it is now and the line is executable.</param>
/// <param name="FailingTests">For <see cref="ChangedLineVerdict.CoveredOnlyByFailing"/>: the failing tests credited with the line.</param>
internal sealed record ChangedLineResult(
    int Line,
    ChangedLineVerdict Verdict,
    UnknownReason Reason = UnknownReason.None,
    LineCoverage? Coverage = null,
    IReadOnlyList<string>? FailingTests = null)
{
    public bool HasPartialBranches =>
        Coverage is { HitCount: > 0, BranchesTotal: > 0 } c && c.BranchesCovered < c.BranchesTotal;
}
