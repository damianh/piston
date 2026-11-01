// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

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
