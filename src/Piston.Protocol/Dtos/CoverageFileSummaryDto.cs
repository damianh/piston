// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>One measured file's coverage totals.</summary>
/// <param name="FilePath">Absolute path of the file.</param>
/// <param name="RelativePath">Path relative to the solution directory, with forward slashes.</param>
/// <param name="Module">Assembly the file was compiled into, when known.</param>
/// <param name="IsStale">The file changed since it was measured.</param>
/// <param name="IsPartial">Measured by a selective run rather than a full run of every test.</param>
/// <param name="UpdatedAt">When the file's coverage was last written.</param>
public sealed record CoverageFileSummaryDto(
    string FilePath,
    string RelativePath,
    string? Module,
    int LinesTotal,
    int LinesCovered,
    int BranchesTotal,
    int BranchesCovered,
    bool IsStale,
    bool IsPartial,
    DateTimeOffset UpdatedAt
);
