// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>A test as it was in the baseline run and in the compared run; either side is null when the test did not run there.</summary>
public sealed record RunComparisonEntry(string Project, string Test, StoredTestResultDto? Before, StoredTestResultDto? After)
{
    public double DeltaMs => (After?.DurationMs ?? 0) - (Before?.DurationMs ?? 0);
}

/// <summary>How one run's per-test results differ from a baseline run's.</summary>
public sealed record RunComparison(
    IReadOnlyList<RunComparisonEntry> NewFailures,
    IReadOnlyList<RunComparisonEntry> Fixed,
    IReadOnlyList<RunComparisonEntry> StillFailing,
    IReadOnlyList<RunComparisonEntry> NewlySlow,
    IReadOnlyList<RunComparisonEntry> Added,
    IReadOnlyList<RunComparisonEntry> Missing)
{
    /// <summary>A test is newly slow when it passed in both runs and took at least this many times as long.</summary>
    public const double SlowdownFactor = 2;

    /// <summary>...and at least this much longer, so tiny tests that jitter are not reported.</summary>
    public const double MinimumSlowdownMs = 100;

    private const string Failed = "Failed";
    private const string Passed = "Passed";

    public bool IsEmpty => NewFailures.Count + Fixed.Count + StillFailing.Count + NewlySlow.Count + Added.Count + Missing.Count == 0;

    /// <summary>Compares <paramref name="after"/> with the earlier <paramref name="before"/>, matching tests by project and name.</summary>
    public static RunComparison Compare(StoredRunDto before, StoredRunDto after)
    {
        var baseline = Index(before);
        var current = Index(after);
        var entries = baseline.Keys.Union(current.Keys)
            .Select(key => new RunComparisonEntry(key.Project, key.Test, baseline.GetValueOrDefault(key), current.GetValueOrDefault(key)))
            .OrderBy(e => e.Project, StringComparer.Ordinal)
            .ThenBy(e => e.Test, StringComparer.Ordinal)
            .ToList();

        return new RunComparison(
            NewFailures: [.. entries.Where(e => e.After?.Status == Failed && e.Before?.Status != Failed)],
            Fixed: [.. entries.Where(e => e.Before?.Status == Failed && e.After?.Status == Passed)],
            StillFailing: [.. entries.Where(e => e.Before?.Status == Failed && e.After?.Status == Failed)],
            NewlySlow: [.. entries
                .Where(e => e.Before?.Status == Passed && e.After?.Status == Passed
                    && e.After.DurationMs >= e.Before.DurationMs * SlowdownFactor
                    && e.DeltaMs >= MinimumSlowdownMs)
                .OrderByDescending(e => e.DeltaMs)],
            Added: [.. entries.Where(e => e.Before is null && e.After is not null)],
            Missing: [.. entries.Where(e => e.Before is not null && e.After is null)]);
    }

    private static Dictionary<(string Project, string Test), StoredTestResultDto> Index(StoredRunDto run)
    {
        var index = new Dictionary<(string, string), StoredTestResultDto>();
        foreach (var result in run.Results)
        {
            index[(result.Project, result.Test)] = result;
        }

        return index;
    }
}
