// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>The outcomes behind a flaky label.</summary>
/// <param name="Source">The source fingerprint the test passed and failed on; null when only retries within one run disagree.</param>
/// <param name="Passed">Passing outcomes counted.</param>
/// <param name="Failed">Failing outcomes counted.</param>
/// <param name="RetryOutcomes">Outcomes counted that came from in-run retries.</param>
/// <param name="OtherWorktrees">Other worktrees of the repository whose outcomes are counted.</param>
/// <param name="Summary">One sentence describing the evidence.</param>
/// <param name="Outcomes">The newest counted outcomes, newest first.</param>
public sealed record FlakyEvidenceDto(
    string? Source,
    int Passed,
    int Failed,
    int RetryOutcomes,
    IReadOnlyList<string> OtherWorktrees,
    string Summary,
    IReadOnlyList<FlakyOutcomeDto> Outcomes);
