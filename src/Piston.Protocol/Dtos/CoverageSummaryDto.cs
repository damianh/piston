// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>The solution-wide coverage report.</summary>
/// <param name="Enabled">Whether the daemon collects coverage.</param>
/// <param name="GeneratedAt">When the report was last written, or null when it is empty.</param>
/// <param name="Files">Every measured file, ordered by path.</param>
public sealed record CoverageSummaryDto(
    bool Enabled,
    DateTimeOffset? GeneratedAt,
    int LinesTotal,
    int LinesCovered,
    int BranchesTotal,
    int BranchesCovered,
    IReadOnlyList<CoverageFileSummaryDto> Files
);
