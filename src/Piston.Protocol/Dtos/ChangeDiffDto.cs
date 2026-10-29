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

/// <summary>One changed file and its hunks.</summary>
/// <param name="FilePath">Absolute path.</param>
/// <param name="RelativePath">Path relative to the solution directory, with forward slashes.</param>
/// <param name="Status"><c>modified</c>, <c>added</c>, <c>deleted</c> or <c>binary</c>.</param>
/// <param name="IsCode">A source file whose added lines get verdicts (C#, F# or Visual Basic).</param>
/// <param name="IsMeasured">The coverage report has the file.</param>
/// <param name="IsStale">The file changed after it was measured, or a range's version of it differs from the working tree.</param>
/// <param name="IsPartial">Only a selective run measured the file.</param>
/// <param name="Totals">The file's added lines per verdict; all zero for non-source files.</param>
/// <param name="Hunks">The file's hunks; empty for binary files and when <paramref name="HunksOmitted"/>.</param>
/// <param name="HunksOmitted">The file's hunks did not fit the reply; only its totals are given.</param>
public sealed record ChangeDiffFileDto(
    string FilePath,
    string RelativePath,
    string Status,
    bool IsCode,
    bool IsMeasured,
    bool IsStale,
    bool IsPartial,
    ChangedLineTotalsDto Totals,
    IReadOnlyList<DiffHunkDto> Hunks,
    bool HunksOmitted);

/// <summary>A hunk of a unified diff, numbered as in <c>@@ -OldStart,OldCount +NewStart,NewCount @@</c>.</summary>
public sealed record DiffHunkDto(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffLineDto> Lines);

/// <summary>One line of a hunk. Only added lines of source files carry a verdict.</summary>
/// <param name="Kind"><c>context</c>, <c>added</c> or <c>removed</c>.</param>
/// <param name="OldLine">Number in the base, for context and removed lines.</param>
/// <param name="NewLine">Number in the changed file, for context and added lines.</param>
/// <param name="Text">The line's text, without its line ending.</param>
/// <param name="Verdict">
/// For added source lines: <c>coveredByPassing</c>, <c>coveredOnlyByFailing</c>, <c>uncovered</c>,
/// <c>nonExecutable</c> or <c>unknown</c>.
/// </param>
/// <param name="Reason">For <c>unknown</c> lines: why, e.g. <c>stale</c> or <c>noTestEvidence</c>.</param>
/// <param name="HitCount">Times the line ran, when it is executable and measured as it is now.</param>
/// <param name="BranchesCovered">Branch outcomes taken, when the line has branches.</param>
/// <param name="BranchesTotal">Branch outcomes, when the line has branches.</param>
public sealed record DiffLineDto(
    string Kind,
    int? OldLine,
    int? NewLine,
    string Text,
    string? Verdict = null,
    string? Reason = null,
    int? HitCount = null,
    int? BranchesCovered = null,
    int? BranchesTotal = null);
