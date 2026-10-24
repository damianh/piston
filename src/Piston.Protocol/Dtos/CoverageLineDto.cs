// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>Coverage status for a single line of source code.</summary>
/// <param name="LineNumber">1-based line number.</param>
/// <param name="HitCount">Times the line was executed.</param>
/// <param name="Status"><c>covered</c>, <c>partial</c> (some branch outcomes not taken) or <c>uncovered</c>.</param>
/// <param name="BranchesCovered">Branch outcomes taken on the line.</param>
/// <param name="BranchesTotal">Branch outcomes on the line; 0 when the line does not branch.</param>
public sealed record CoverageLineDto(
    int LineNumber,
    int HitCount,
    string Status,
    int BranchesCovered = 0,
    int BranchesTotal = 0
);
