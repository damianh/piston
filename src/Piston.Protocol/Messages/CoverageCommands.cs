// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Request coverage data for a single source file.</summary>
/// <param name="FilePath">Absolute path, or a path relative to the solution directory.</param>
/// <param name="IncludeSource">Also return the file's text, for rendering it with line coverage.</param>
public sealed record GetFileCoverageCommand(string FilePath, bool IncludeSource = false);

/// <summary>Export the solution-wide coverage report.</summary>
/// <param name="Format"><c>lcov</c> or <c>cobertura</c>.</param>
/// <param name="ExportId">Snapshot identifier from the previous chunk; null starts a new export.</param>
/// <param name="Offset">Character offset requested by the previous chunk's <c>nextOffset</c>.</param>
public sealed record ExportCoverageCommand(string Format, string? ExportId = null, int Offset = 0);

/// <summary>Request the tests recorded as executing a source file.</summary>
/// <param name="FilePath">Absolute path, or a path relative to the solution directory.</param>
public sealed record GetCoveringTestsCommand(string FilePath);

/// <summary>Request a verdict for each changed line of the changed source files.</summary>
/// <param name="Base"><c>working</c>, <c>branch</c> or <c>range</c>; null means working, or branch when <paramref name="BaseRef"/> is set.</param>
/// <param name="BaseRef">For branch: the branch or commit to compare against. For range: the range or commit.</param>
public sealed record GetChangedLineCoverageCommand(string? Base = null, string? BaseRef = null);

public sealed record GetStoredRunCommand(string RunId);

/// <param name="Test">Fully-qualified name; a multi-targeted test's results under every framework match.</param>
/// <param name="Project">Test project name; null matches any.</param>
public sealed record GetTestHistoryCommand(string Test, string? ProjectId = null);

/// <summary>Request a page of the session timeline.</summary>
/// <param name="AfterSequence">Return only runs requested after this sequence (the last entry of the previous page); null starts from the oldest.</param>
public sealed record GetSessionTimelineCommand(long? AfterSequence = null);
