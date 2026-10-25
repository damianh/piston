// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A verdict for each changed line of each changed source file.</summary>
/// <param name="Enabled">False when coverage is disabled; everything else is then empty.</param>
/// <param name="Base">What the changes were compared against, e.g. <c>working</c> or <c>branch (merge base of main)</c>.</param>
/// <param name="Totals">Changed lines per verdict across every file.</param>
/// <param name="UnknownReasons">Unknown lines per reason (<c>stale</c>, <c>notMeasured</c>, <c>noTestEvidence</c>, <c>mixedEvidence</c>, <c>testsNotRun</c>, <c>outdatedEvidence</c>).</param>
/// <param name="Files">The changed source files, ordered by path.</param>
/// <param name="Warnings">Caveats, such as a missing merge base or a run in progress.</param>
public sealed record ChangedLineCoverageDto(
    bool Enabled,
    string? Base,
    ChangedLineTotalsDto Totals,
    IReadOnlyDictionary<string, int> UnknownReasons,
    IReadOnlyList<ChangedLineFileDto> Files,
    IReadOnlyList<string> Warnings);

/// <summary>Changed lines per verdict.</summary>
public sealed record ChangedLineTotalsDto(
    int ChangedLines, int CoveredByPassing, int CoveredOnlyByFailing, int Uncovered, int NonExecutable, int Unknown);

/// <summary>The verdicts for one changed source file.</summary>
/// <param name="FilePath">Absolute path.</param>
/// <param name="RelativePath">Path relative to the solution directory, with forward slashes.</param>
/// <param name="IsMeasured">The coverage report has the file.</param>
/// <param name="IsStale">The file changed after it was measured, so every changed line is unknown.</param>
/// <param name="IsPartial">Only a selective run measured the file.</param>
/// <param name="Totals">The file's changed lines per verdict.</param>
/// <param name="Lines">Each changed line, in line order.</param>
public sealed record ChangedLineFileDto(
    string FilePath,
    string RelativePath,
    bool IsMeasured,
    bool IsStale,
    bool IsPartial,
    ChangedLineTotalsDto Totals,
    IReadOnlyList<ChangedLineDto> Lines);

/// <summary>The verdict for one changed line.</summary>
/// <param name="Line">1-based line number in the current file.</param>
/// <param name="Verdict"><c>coveredByPassing</c>, <c>coveredOnlyByFailing</c>, <c>uncovered</c>, <c>nonExecutable</c> or <c>unknown</c>.</param>
/// <param name="Reason">Why the verdict is <c>unknown</c>; null otherwise.</param>
/// <param name="HitCount">Aggregate hits, when the file was measured as it is now and the line is executable.</param>
/// <param name="BranchesCovered">Branch outcomes taken on the line.</param>
/// <param name="BranchesTotal">Branch outcomes on the line; 0 when it has no branches or no coverage.</param>
/// <param name="FailingTests">For <c>coveredOnlyByFailing</c>: the failing tests credited with the line.</param>
public sealed record ChangedLineDto(
    int Line,
    string Verdict,
    string? Reason,
    int? HitCount,
    int BranchesCovered,
    int BranchesTotal,
    IReadOnlyList<string>? FailingTests);
