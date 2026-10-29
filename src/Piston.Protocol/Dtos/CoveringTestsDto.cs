// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>An inclusive, 1-based range of source lines.</summary>
public sealed record LineRangeDto(int Start, int End);

/// <summary>A test recorded as executing a source file.</summary>
/// <param name="TestFqn">Fully-qualified test name.</param>
/// <param name="ProjectPath">Absolute path of the test project.</param>
/// <param name="Granularity">
/// <c>run</c> (every test in the project's run measured together), <c>class</c> (the test's class run alone) or
/// <c>test</c> (the test alone). Run- and class-level evidence over-attributes.
/// </param>
/// <param name="ExecutedLines">Executed method ranges, numbered as in the content that was measured.</param>
/// <param name="IsCurrent">The ranges line up with the file as it is now.</param>
public sealed record CoveringTestDto(
    string TestFqn,
    string ProjectPath,
    string Granularity,
    IReadOnlyList<LineRangeDto> ExecutedLines,
    bool IsCurrent);

/// <summary>The tests recorded as executing a source file.</summary>
/// <param name="FilePath">Absolute path of the file.</param>
/// <param name="Tests">The tests, ordered by project then name. Empty when none were recorded.</param>
public sealed record CoveringTestsDto(string FilePath, IReadOnlyList<CoveringTestDto> Tests);

/// <summary>Why a run selected a test.</summary>
/// <param name="FilePath">Absolute path of the changed file.</param>
/// <param name="ChangedLines">The changed lines, e.g. <c>12-14</c>, numbered as in the recorded snapshot.</param>
/// <param name="ExecutedLines">The executed range the change overlapped; null when the test was selected because it executed the file at all.</param>
/// <param name="Granularity"><c>run</c>, <c>class</c> or <c>test</c>: how precisely the evidence attributes the file to the test.</param>
/// <param name="ProjectPath">Absolute path of the test project.</param>
public sealed record TestSelectionReasonDto(
    string FilePath,
    string ChangedLines,
    string? ExecutedLines,
    string Granularity,
    string ProjectPath,
    IReadOnlyList<BranchLineDto>? Branches = null);

/// <summary>Branch outcomes attributed to a measured test on one source line.</summary>
public sealed record BranchLineDto(int Line, int Covered, int Total);
