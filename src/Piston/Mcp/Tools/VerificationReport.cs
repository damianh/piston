// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text;
using Piston.Engine.Models;
using Piston.Engine.Verification;

namespace Piston.Mcp.Tools;

/// <summary>Compact, agent-oriented text for a <see cref="VerificationResult"/>.</summary>
internal static class VerificationReport
{
    internal const int MaxListedItems = 20;
    internal const int MaxMessageLength = 400;

    public static string Format(VerificationResult result)
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine($"Verdict: {result.Verdict}");
        if (result.Reason is { } reason)
        {
            _ = sb.AppendLine($"Reason: {reason}");
        }

        _ = sb.AppendLine($"Counts as full verification: {(result.Verdict == VerificationVerdict.Passed ? "yes" : "no")}");
        _ = sb.AppendLine($"Reused: {(result.Source == VerificationSource.Reused ? "yes" : "no")} ({result.Source}), attempts: {result.Attempts}");
        var fp = result.SourceFingerprint;
        _ = sb.AppendLine($"Source: {(fp is null ? "unknown" : $"{fp.Algorithm}:{fp.Value} ({fp.FileCount} files)")}");

        if (result.Run is not { } run)
        {
            return sb.ToString().TrimEnd();
        }

        var tests = run.Tests.ToList();
        _ = sb.AppendLine(
            $"Run: {run.RunId} | Trigger: {run.Trigger} | Status: {run.Status} | Scope: {run.Scope?.ToString() ?? "Unknown"} | " +
            $"Freshness: {run.Freshness}");
        _ = sb.AppendLine(
            $"Tests: {tests.Count} (Passed: {tests.Count(t => t.Status == TestStatus.Passed)}, " +
            $"Failed: {tests.Count(t => t.Status == TestStatus.Failed)}, Skipped: {tests.Count(t => t.Status == TestStatus.Skipped)})");

        // Only mention admission when the run actually waited for machine-wide capacity.
        if (run.Admission is { } admission
            && (admission.State == TestRunAdmissionState.Queued || admission.AdmittedAt - admission.QueuedAt >= TimeSpan.FromSeconds(1)))
        {
            _ = sb.AppendLine(TestTools.DescribeAdmission(admission));
        }

        if (result.Verdict == VerificationVerdict.BuildFailed && run.Build is { } build)
        {
            AppendList(sb, "Build errors", build.Errors, e => e);
        }

        if (result.Verdict is VerificationVerdict.TestsFailed)
        {
            AppendList(sb, "Failed tests", tests.Where(t => t.Status == TestStatus.Failed).ToList(),
                t => t.ErrorMessage is { } message ? $"{t.FullyQualifiedName}: {Cap(message)}" : t.FullyQualifiedName);
        }

        if (result.Verdict is VerificationVerdict.Incomplete)
        {
            var problems = run.Projects.Where(p => p.Status != ProjectRunStatus.Completed || p.TestCount == 0).ToList();
            AppendList(sb, "Projects without complete results", problems,
                p => $"{p.ProjectPath}: {(p.Status == ProjectRunStatus.Completed ? "no test results" : p.Status.ToString())}");
            AppendList(sb, "Tests without complete results",
                tests.Where(t => t.Status is TestStatus.NotRun or TestStatus.Running).ToList(),
                t => $"{t.FullyQualifiedName}: {t.Status}");
            AppendList(sb, "Duplicate test IDs", run.DuplicateTestIds, id => id);
        }

        if (run.RunnerError is { } runnerError && result.Verdict != VerificationVerdict.Passed)
        {
            _ = sb.AppendLine($"Runner error: {Cap(runnerError)}");
        }

        _ = sb.Append($"Details: get_test_run(\"{run.RunId}\")");
        return sb.ToString();
    }

    private static void AppendList<T>(StringBuilder sb, string title, IReadOnlyList<T> items, Func<T, string> describe)
    {
        if (items.Count == 0)
        {
            return;
        }

        _ = sb.AppendLine($"{title} ({items.Count}):");
        foreach (var item in items.Take(MaxListedItems))
        {
            _ = sb.AppendLine($"  - {Cap(describe(item))}");
        }

        if (items.Count > MaxListedItems)
        {
            _ = sb.AppendLine($"  ... {items.Count - MaxListedItems} more");
        }
    }

    private static string Cap(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxMessageLength ? line : line[..MaxMessageLength] + "…";
    }
}
