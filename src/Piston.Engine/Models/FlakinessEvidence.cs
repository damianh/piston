// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>
/// Why a test is labelled flaky: it both passed and failed on one source fingerprint (in this worktree or another
/// worktree of the repository), or within one run's retries of the same build.
/// </summary>
/// <param name="Source">The source fingerprint both outcomes ran against; null when the evidence is one run's retries without a current fingerprint.</param>
/// <param name="Passed">Passes recorded on that source (or in that run).</param>
/// <param name="Failed">Failures recorded on that source (or in that run).</param>
/// <param name="RetryOutcomes">How many of those outcomes came from in-run retries.</param>
/// <param name="OtherWorktrees">Other worktrees of the repository that contributed outcomes, by folder name.</param>
/// <param name="Outcomes">The outcomes, newest first, capped at <see cref="MaxOutcomes"/>.</param>
public sealed record FlakinessEvidence(
    string? Source,
    int Passed,
    int Failed,
    int RetryOutcomes,
    IReadOnlyList<string> OtherWorktrees,
    IReadOnlyList<FlakyOutcome> Outcomes)
{
    /// <summary>Most outcomes listed in <see cref="Outcomes"/>.</summary>
    public const int MaxOutcomes = 10;

    /// <summary>"Passed 3 and failed 2 times on source 0123456789ab (1 from in-run retries; other worktrees: a, b)."</summary>
    public string Describe()
    {
        var where = Source is { } source
            ? $"on source {(source.Length > 12 ? source[..12] : source)}"
            : $"within run {Outcomes.FirstOrDefault()?.RunId} (retries of the same build)";
        var details = new List<string>();
        if (RetryOutcomes > 0)
        {
            details.Add($"{RetryOutcomes} from in-run retries");
        }

        if (OtherWorktrees.Count > 0)
        {
            details.Add($"other worktrees: {string.Join(", ", OtherWorktrees)}");
        }

        return $"Passed {Passed} and failed {Failed} time(s) {where}{(details.Count > 0 ? $" ({string.Join("; ", details)})" : "")}.";
    }

    /// <summary>"3 passed/2 failed on 0123456789ab, 1 from retries, other worktrees: a, b".</summary>
    public string Brief()
    {
        var text = $"{Passed} passed/{Failed} failed {(Source is { } source ? $"on {(source.Length > 12 ? source[..12] : source)}" : "in retries of one run")}";
        if (Source is not null && RetryOutcomes > 0)
        {
            text += $", {RetryOutcomes} from retries";
        }

        return OtherWorktrees.Count > 0 ? $"{text}, other worktrees: {string.Join(", ", OtherWorktrees)}" : text;
    }
}
