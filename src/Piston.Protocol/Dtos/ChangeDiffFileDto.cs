// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

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
/// <param name="IsDiverged">
/// A range's version of the file differs from the working tree, so its line numbers do not match the coverage
/// evidence, which is numbered for the file on disk.
/// </param>
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
    bool HunksOmitted,
    bool IsDiverged = false);
