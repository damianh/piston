// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Protocol.Messages;

namespace Piston.Web.Models;

/// <summary>The run state shown outside the page: the tab title, the favicon and screen reader announcements.</summary>
public static class DashboardStatus
{
    /// <summary>The favicon state: <c>idle</c>, <c>busy</c>, <c>pass</c> or <c>fail</c>.</summary>
    public static string Icon(StateSnapshotNotification? snapshot, bool connected)
    {
        if (!connected || snapshot is null)
        {
            return "idle";
        }

        if (IsBusy(snapshot.Phase))
        {
            return "busy";
        }

        var (passed, failed, _) = Counts(snapshot.Suites);
        return snapshot.Phase == PistonPhaseDto.Error || failed > 0 ? "fail"
            : passed > 0 ? "pass"
            : "idle";
    }

    /// <summary>The tab title: the state first, so it stays readable in a narrow tab.</summary>
    public static string Title(StateSnapshotNotification? snapshot, bool connected)
    {
        var solution = snapshot?.SolutionPath is { Length: > 0 } path ? Path.GetFileNameWithoutExtension(path) : null;
        var suffix = solution is null ? "Piston" : $"{solution} · Piston";
        if (!connected)
        {
            return $"Disconnected · {suffix}";
        }

        if (snapshot is null)
        {
            return suffix;
        }

        var (passed, failed, _) = Counts(snapshot.Suites);
        var state = snapshot.Phase switch
        {
            PistonPhaseDto.Building => "⟳ Building",
            PistonPhaseDto.Analyzing => "⟳ Analyzing",
            PistonPhaseDto.Testing => "⟳ Testing",
            PistonPhaseDto.Error => "✖ Build failed",
            _ when failed > 0 => $"✖ {failed:N0} failed",
            _ when passed > 0 => $"✔ {passed:N0} passed",
            _ => null,
        };
        return state is null ? suffix : $"{state} · {suffix}";
    }

    /// <summary>
    /// What to announce when the phase moves from <paramref name="previous"/> to <paramref name="current"/>:
    /// the start and the outcome of a run, or <see langword="null"/>.
    /// </summary>
    public static string? Announcement(StateSnapshotNotification? previous, StateSnapshotNotification current)
    {
        var wasBusy = previous is not null && IsBusy(previous.Phase);
        var isBusy = IsBusy(current.Phase);
        if (!wasBusy && isBusy)
        {
            return "Run started.";
        }

        if (!wasBusy || isBusy)
        {
            return null;
        }

        if (current.Phase == PistonPhaseDto.Error)
        {
            return "Run finished: the build failed.";
        }

        var (passed, failed, skipped) = Counts(current.Suites);
        var parts = new List<string> { $"{failed:N0} failed", $"{passed:N0} passed" };
        if (skipped > 0)
        {
            parts.Add($"{skipped:N0} skipped");
        }

        return $"Run finished: {string.Join(", ", parts)}.";
    }

    /// <summary>A run of every failed test, or <see langword="null"/> when none failed.</summary>
    public static RunTestsCommand? RunFailed(IReadOnlyList<TestSuiteDto> suites) => RunTestsCommand.ForFailed(suites);

    /// <summary>The number of distinct failed tests <see cref="RunFailed"/> runs.</summary>
    public static int FailedCount(IReadOnlyList<TestSuiteDto> suites) =>
        RunFailed(suites)?.Tests?.Count ?? 0;

    private static bool IsBusy(PistonPhaseDto phase) =>
        phase is PistonPhaseDto.Analyzing or PistonPhaseDto.Building or PistonPhaseDto.Testing;

    private static (int Passed, int Failed, int Skipped) Counts(IReadOnlyList<TestSuiteDto> suites)
    {
        int passed = 0, failed = 0, skipped = 0;
        foreach (var test in suites.SelectMany(s => s.Tests))
        {
            switch (test.Status)
            {
                case TestStatusDto.Passed: passed++; break;
                case TestStatusDto.Failed: failed++; break;
                case TestStatusDto.Skipped: skipped++; break;
            }
        }

        return (passed, failed, skipped);
    }
}
