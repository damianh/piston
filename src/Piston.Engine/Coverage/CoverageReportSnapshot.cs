// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>
/// The solution-wide coverage report: every measured file's totals.
/// </summary>
public sealed record CoverageReportSnapshot(IReadOnlyList<CoverageFileSummary> Files)
{
    public int LinesTotal => Files.Sum(f => f.LinesTotal);

    public int LinesCovered => Files.Sum(f => f.LinesCovered);

    public int BranchesTotal => Files.Sum(f => f.BranchesTotal);

    public int BranchesCovered => Files.Sum(f => f.BranchesCovered);

    /// <summary>When the report was last written, or null when it is empty.</summary>
    public DateTimeOffset? GeneratedAt => Files.Count == 0 ? null : Files.Max(f => f.UpdatedAt);
}
