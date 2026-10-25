// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>Presentation helpers for in-run retries and the evidence behind flaky labels.</summary>
public static class TestRetries
{
    /// <summary>"Passed on attempt 2 after 1 failure" or "Failed all 3 attempts"; null when the test ran once.</summary>
    public static string? Describe(TestResultDto test) =>
        Describe(test.Status == TestStatusDto.Passed, test.Status == TestStatusDto.Failed, test.Attempts?.Count ?? 0,
            test.Attempts?.Count(a => a.Status == TestStatusDto.Failed) ?? 0);

    /// <inheritdoc cref="Describe(TestResultDto)"/>
    public static string? Describe(StoredTestResultDto test) =>
        Describe(test.Status == "Passed", test.Status == "Failed", test.Attempts?.Count ?? 0,
            test.Attempts?.Count(a => a.Status == "Failed") ?? 0);

    private static string? Describe(bool passed, bool failed, int earlier, int failures) => earlier switch
    {
        0 => null,
        _ when passed => $"Passed on attempt {earlier + 1} after {failures} failure{(failures == 1 ? "" : "s")}",
        _ when failed => $"Failed all {earlier + 1} attempts",
        _ => $"{earlier + 1} attempts",
    };

    /// <summary>The first line of an attempt's error message, or its status when it has none.</summary>
    public static string FirstLine(string? message, string status)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return status;
        }

        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }

    /// <summary>The tooltip of a flaky badge: the evidence summary, or a generic definition when none was sent.</summary>
    public static string FlakyTitle(FlakyEvidenceDto? evidence) =>
        evidence is null ? "Flaky: passed and failed on the same source" : $"Flaky: {evidence.Summary}";

    /// <summary>One line per counted outcome, newest first: when, status, run, retry and worktree.</summary>
    public static string DescribeOutcomes(FlakyEvidenceDto evidence) => string.Join(Environment.NewLine, evidence.Outcomes.Select(o =>
        string.Create(CultureInfo.InvariantCulture,
            $"{o.At.ToLocalTime():yyyy-MM-dd HH:mm:ss} {o.Status} run {o.RunId}{(o.Attempt > 0 ? $" retry {o.Attempt}" : "")}{(o.Worktree is { } w ? $" in worktree {w}" : "")}")));
}
