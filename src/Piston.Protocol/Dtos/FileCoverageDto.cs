// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A source file's coverage: totals, per-line coverage and optionally its source text.</summary>
/// <param name="FilePath">Absolute path of the file.</param>
/// <param name="Lines">Coverable lines, ordered by line number. Empty when the file was not measured.</param>
/// <param name="Summary">The file's totals, or null when the file was not measured.</param>
/// <param name="Source">The file's lines of text, when requested and the file was measured.</param>
public sealed record FileCoverageDto(
    string FilePath,
    IReadOnlyList<CoverageLineDto> Lines,
    CoverageFileSummaryDto? Summary = null,
    IReadOnlyList<string>? Source = null
);
