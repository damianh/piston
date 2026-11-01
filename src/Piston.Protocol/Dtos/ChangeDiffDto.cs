// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>
/// The unified diff of the changes against a git base, with a verdict on each added line of the changed source
/// files. Hunks are bounded to fit a message; the totals always count every changed line.
/// </summary>
/// <param name="Enabled">False when coverage is disabled; everything else is then empty.</param>
/// <param name="Base">What the changes were compared against, e.g. <c>HEAD</c> or a merge-base commit.</param>
/// <param name="Totals">Changed source lines per verdict across every file, including omitted files.</param>
/// <param name="UnknownReasons">Unknown lines per reason, as in <see cref="ChangedLineCoverageDto.UnknownReasons"/>.</param>
/// <param name="Files">Every changed file, source or not, ordered by path.</param>
/// <param name="FilesOmitted">Changed files left out of <paramref name="Files"/> to bound the reply.</param>
/// <param name="Warnings">Caveats, such as a missing merge base, a run in progress or omitted hunks.</param>
public sealed record ChangeDiffDto(
    bool Enabled,
    string? Base,
    ChangedLineTotalsDto Totals,
    IReadOnlyDictionary<string, int> UnknownReasons,
    IReadOnlyList<ChangeDiffFileDto> Files,
    int FilesOmitted,
    IReadOnlyList<string> Warnings);
