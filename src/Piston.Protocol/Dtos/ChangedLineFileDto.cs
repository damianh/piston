// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>The verdicts for one changed source file.</summary>
/// <param name="FilePath">Absolute path.</param>
/// <param name="RelativePath">Path relative to the solution directory, with forward slashes.</param>
/// <param name="IsMeasured">The coverage report has the file.</param>
/// <param name="IsStale">
/// The file changed after it was measured. Its changed lines are unknown, except those in unchanged C# members,
/// which keep their remapped coverage.
/// </param>
/// <param name="IsPartial">Only a selective run measured the file.</param>
/// <param name="Totals">The file's changed lines per verdict.</param>
/// <param name="CoveredByPassing">Changed lines that ran, with every credited test passing.</param>
/// <param name="CoveredOnlyByFailing">Changed lines that ran, with every credited test failing.</param>
/// <param name="Uncovered">Executable changed lines that no test ran.</param>
/// <param name="NonExecutable">Changed lines with no executable code.</param>
/// <param name="Unknown">Changed lines the evidence proves nothing about, grouped by reason.</param>
/// <param name="PartialBranches">Changed lines that ran with only some of their branch outcomes taken.</param>
/// <param name="FailingTests">The failing tests credited with the file's <c>coveredOnlyByFailing</c> lines, ordered and capped.</param>
/// <param name="FailingTestsOmitted">Failing tests left out of <paramref name="FailingTests"/>.</param>
/// <param name="DetailOmitted">The file's line detail did not fit the reply; only its totals are given.</param>
public sealed record ChangedLineFileDto(
    string FilePath,
    string RelativePath,
    bool IsMeasured,
    bool IsStale,
    bool IsPartial,
    ChangedLineTotalsDto Totals,
    IReadOnlyList<LineRangeDto> CoveredByPassing,
    IReadOnlyList<LineRangeDto> CoveredOnlyByFailing,
    IReadOnlyList<LineRangeDto> Uncovered,
    IReadOnlyList<LineRangeDto> NonExecutable,
    IReadOnlyList<ChangedLineUnknownDto> Unknown,
    IReadOnlyList<ChangedLineBranchesDto> PartialBranches,
    IReadOnlyList<string> FailingTests,
    int FailingTestsOmitted,
    bool DetailOmitted);
