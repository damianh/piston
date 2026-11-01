// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>
/// Stored coverage totals for one file in the solution-wide coverage report.
/// </summary>
/// <param name="FilePath">Absolute path of the source file.</param>
/// <param name="Module">Assembly the file was compiled into, when known.</param>
/// <param name="LinesTotal">Coverable lines.</param>
/// <param name="LinesCovered">Coverable lines executed at least once.</param>
/// <param name="BranchesTotal">Branch outcomes.</param>
/// <param name="BranchesCovered">Branch outcomes taken.</param>
/// <param name="IsStale">The file changed after it was measured and no run has re-measured it.</param>
/// <param name="IsPartial">The data comes from a selective run rather than a full run of every test.</param>
/// <param name="ContentHash">SHA-256 of the content the data was measured against.</param>
/// <param name="RunId">Coverage run that last wrote the file.</param>
/// <param name="UpdatedAt">When the file was last written.</param>
public sealed record CoverageFileSummary(
    string FilePath,
    string? Module,
    int LinesTotal,
    int LinesCovered,
    int BranchesTotal,
    int BranchesCovered,
    bool IsStale,
    bool IsPartial,
    string? ContentHash,
    long RunId,
    DateTimeOffset UpdatedAt,
    string? SnapshotHash = null);
