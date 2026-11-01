// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>An exported coverage report.</summary>
/// <param name="Content">The report text.</param>
/// <param name="FileCount">Source files in the report.</param>
/// <param name="StaleFilesSkipped">Files left out because they changed after they were measured.</param>
public sealed record CoverageExport(string Content, int FileCount, int StaleFilesSkipped);
