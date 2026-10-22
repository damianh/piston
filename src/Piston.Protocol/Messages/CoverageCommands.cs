// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Request coverage data for a single source file.</summary>
/// <param name="FilePath">Absolute path, or a path relative to the solution directory.</param>
/// <param name="IncludeSource">Also return the file's text, for rendering it with line coverage.</param>
public sealed record GetFileCoverageCommand(string FilePath, bool IncludeSource = false);

/// <summary>Export the solution-wide coverage report.</summary>
/// <param name="Format"><c>lcov</c> or <c>cobertura</c>.</param>
public sealed record ExportCoverageCommand(string Format);

/// <summary>Request the tests recorded as executing a source file.</summary>
/// <param name="FilePath">Absolute path, or a path relative to the solution directory.</param>
public sealed record GetCoveringTestsCommand(string FilePath);
