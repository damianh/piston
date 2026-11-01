// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>
/// One file's coverage totals and per-line coverage.
/// </summary>
public sealed record FileCoverageSnapshot(CoverageFileSummary File, IReadOnlyList<LineCoverage> Lines)
{
    /// <summary>Current source line ranges for which stored coverage could not be validated.</summary>
    public IReadOnlyList<CoveredLineRange> StaleLines { get; init; } = [];
}
