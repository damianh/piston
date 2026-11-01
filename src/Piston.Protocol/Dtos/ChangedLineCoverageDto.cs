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
