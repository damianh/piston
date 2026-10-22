// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>The coverage report exported as LCOV or Cobertura.</summary>
/// <param name="Format"><c>lcov</c> or <c>cobertura</c>.</param>
/// <param name="FileName">Conventional file name for the format.</param>
/// <param name="Content">The report text.</param>
/// <param name="FileCount">Source files in the report.</param>
/// <param name="StaleFilesSkipped">Files left out because they changed after they were measured.</param>
public sealed record CoverageExportDto(string Format, string FileName, string Content, int FileCount, int StaleFilesSkipped);
