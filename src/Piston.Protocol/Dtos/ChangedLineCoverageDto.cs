// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>
/// The verdicts for the changed lines of each changed source file, as line ranges per verdict. File detail is
/// bounded to fit a message; the totals always count every changed line.
/// </summary>
/// <param name="Enabled">False when coverage is disabled; everything else is then empty.</param>
/// <param name="Base">What the changes were compared against, e.g. <c>working</c> or <c>branch (merge base of main)</c>.</param>
/// <param name="Totals">Changed lines per verdict across every file, including omitted files.</param>
/// <param name="UnknownReasons">Unknown lines per reason (<c>stale</c>, <c>notMeasured</c>, <c>noTestEvidence</c>, <c>mixedEvidence</c>, <c>testsNotRun</c>, <c>outdatedEvidence</c>, <c>resultsUpdating</c>).</param>
/// <param name="Files">The changed source files, ordered by path.</param>
/// <param name="FilesOmitted">Changed source files left out of <paramref name="Files"/> to bound the reply.</param>
/// <param name="Warnings">Caveats, such as a missing merge base, a run in progress or omitted detail.</param>
public sealed record ChangedLineCoverageDto(
    bool Enabled,
    string? Base,
    ChangedLineTotalsDto Totals,
    IReadOnlyDictionary<string, int> UnknownReasons,
    IReadOnlyList<ChangedLineFileDto> Files,
    int FilesOmitted,
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
/// <param name="CoveredByPassing">Changed lines that ran, with every credited test passing.</param>
/// <param name="CoveredOnlyByFailing">Changed lines that ran, with every credited test failing.</param>
/// <param name="Uncovered">Executable changed lines that no test ran.</param>
/// <param name="NonExecutable">Changed lines with no executable code.</param>
/// <param name="Unknown">Changed lines the evidence proves nothing about, grouped by reason.</param>
/// <param name="PartialBranches">Changed lines that ran with only some of their branch outcomes taken.</param>
/// <param name="FailingTests">The failing tests credited with the file's <c>coveredOnlyByFailing</c> lines, ordered and capped.</param>
/// <param name="FailingTestsOmitted">Failing tests left out of <paramref name="FailingTests"/>.</param>
/// <param name="DetailOmitted">The file's line detail did not fit the reply; only its totals are given.</param>
public sealed record ChangedLineFileDto(
    string FilePath,
    string RelativePath,
    bool IsMeasured,
    bool IsStale,
    bool IsPartial,
    ChangedLineTotalsDto Totals,
    IReadOnlyList<LineRangeDto> CoveredByPassing,
    IReadOnlyList<LineRangeDto> CoveredOnlyByFailing,
    IReadOnlyList<LineRangeDto> Uncovered,
    IReadOnlyList<LineRangeDto> NonExecutable,
    IReadOnlyList<ChangedLineUnknownDto> Unknown,
    IReadOnlyList<ChangedLineBranchesDto> PartialBranches,
    IReadOnlyList<string> FailingTests,
    int FailingTestsOmitted,
    bool DetailOmitted);

/// <summary>Unknown changed lines that share a reason.</summary>
/// <param name="Reason">Why the lines are unknown, e.g. <c>stale</c> or <c>mixedEvidence</c>.</param>
/// <param name="Lines">The lines, as inclusive ranges.</param>
public sealed record ChangedLineUnknownDto(string Reason, IReadOnlyList<LineRangeDto> Lines);

/// <summary>A changed line that ran with only some of its branch outcomes taken.</summary>
public sealed record ChangedLineBranchesDto(int Line, int BranchesCovered, int BranchesTotal);
