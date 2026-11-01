// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A test recorded as executing a source file.</summary>
/// <param name="TestFqn">Fully-qualified test name.</param>
/// <param name="ProjectPath">Absolute path of the test project.</param>
/// <param name="Granularity">
/// <c>run</c> (every test in the project's run measured together), <c>class</c> (the test's class run alone) or
/// <c>test</c> (the test alone). Run- and class-level evidence over-attributes.
/// </param>
/// <param name="ExecutedLines">Executed method ranges, numbered as in the content that was measured.</param>
/// <param name="IsCurrent">The ranges line up with the file as it is now.</param>
/// <param name="Outcome">
/// The test's latest outcome, as changed-line verdicts judge it: <c>passed</c>, <c>failed</c>, <c>skipped</c>
/// (skipped everywhere it ran) or <c>noResult</c>. Null from daemons that predate it.
/// </param>
public sealed record CoveringTestDto(
    string TestFqn,
    string ProjectPath,
    string Granularity,
    IReadOnlyList<LineRangeDto> ExecutedLines,
    bool IsCurrent,
    string? Outcome = null);
