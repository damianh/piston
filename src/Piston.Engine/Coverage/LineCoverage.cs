// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>
/// Coverage data for a single line in a source file.
/// </summary>
/// <param name="LineNumber">1-based line number.</param>
/// <param name="HitCount">Number of times the line was executed.</param>
/// <param name="BranchesCovered">Branch outcomes taken on this line (0 when the line has no branches).</param>
/// <param name="BranchesTotal">Branch outcomes on this line (0 when the line has no branches).</param>
public sealed record LineCoverage(int LineNumber, int HitCount, int BranchesCovered = 0, int BranchesTotal = 0);
