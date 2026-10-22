// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Impact;

namespace Piston.Engine.Coverage;

/// <summary>
/// Parsed representation of a Cobertura XML coverage report.
/// </summary>
internal sealed record CoberturaReport(IReadOnlyList<FileCoverage> Files);

/// <summary>
/// Coverage data for a single source file.
/// </summary>
/// <param name="FilePath">Absolute path of the source file.</param>
/// <param name="Lines">Coverable lines, ordered by line number.</param>
/// <param name="Module">The Cobertura package (assembly) the file was compiled into, when known.</param>
internal sealed record FileCoverage(string FilePath, IReadOnlyList<LineCoverage> Lines, string? Module = null)
{
    /// <summary>
    /// Line ranges of the methods the tests entered, merged. Null when the report lists no methods, in which
    /// case the covered lines stand in for them.
    /// </summary>
    public IReadOnlyList<LineSpan>? ExecutedSpans { get; init; }

    /// <summary>The executed method ranges, or the covered lines when the report has no method detail.</summary>
    public IReadOnlyList<LineSpan> GetExecutedSpans() => ExecutedSpans
        ?? LineSpan.Normalize(Lines.Where(l => l.HitCount > 0).Select(l => new LineSpan(l.LineNumber, l.LineNumber)));
}

/// <summary>
/// Coverage data for a single line in a source file.
/// </summary>
/// <param name="LineNumber">1-based line number.</param>
/// <param name="HitCount">Number of times the line was executed.</param>
/// <param name="BranchesCovered">Branch outcomes taken on this line (0 when the line has no branches).</param>
/// <param name="BranchesTotal">Branch outcomes on this line (0 when the line has no branches).</param>
public sealed record LineCoverage(int LineNumber, int HitCount, int BranchesCovered = 0, int BranchesTotal = 0);
