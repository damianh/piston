// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

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
    string? Member = null,
    IReadOnlyList<BranchLineDto>? Branches = null);
