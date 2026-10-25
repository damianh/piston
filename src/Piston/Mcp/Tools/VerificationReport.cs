// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Piston.Engine.Verification;

namespace Piston.Mcp.Tools;

/// <summary>Compact, agent-oriented text for a <see cref="VerificationResult"/>.</summary>
internal static class VerificationReport
{
    internal const int MaxListedItems = 20;
    internal const int MaxMessageLength = 400;
    internal const int MaxBuildErrorLength = 800;
    private const int BuildErrorHeadLength = 200;

    public static string Format(VerificationResult result, string? solutionPath = null)
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine($"Verdict: {result.Verdict}");
        if (solutionPath is not null)
        {
            _ = sb.AppendLine($"Solution: {solutionPath}");
        }
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
            AppendList(sb, "Build errors", build.Errors, e => e, CapBuildError);
            if (build.OmittedErrors > 0)
            {
                _ = sb.AppendLine($"  ... {build.OmittedErrors} more build error(s) omitted");
            }
        }

        var classifications = run.FailureClassifications
            .GroupBy(c => (c.Project, c.Test))
            .ToDictionary(g => g.Key, g => g.First());
        if (result.Verdict is VerificationVerdict.TestsFailed)
        {
            var failedKeys = run.Suites
                .SelectMany(s => s.Tests.Where(t => t.Status == TestStatus.Failed).Select(t => (s.Name, t.ResultKey)))
                .ToHashSet();
            var failedClassifications = run.FailureClassifications.Where(c => failedKeys.Contains((c.Project, c.Test))).ToList();
            if (failedClassifications.Count > 0)
            {
                _ = sb.AppendLine(DescribeFailureHistory(failedClassifications));
            }

            FailureKind? KindOf(string project, TestResult t) =>
                classifications.TryGetValue((project, t.ResultKey), out var c) ? c.Kind : null;
            var repeatedKeys = tests.GroupBy(t => t.ResultKey, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            AppendList(sb, "Failed tests", run.Suites
                    .SelectMany(s => s.Tests.Select(t => (Project: s.Name, Test: t)))
                    .Where(t => t.Test.Status == TestStatus.Failed)
                    .OrderBy(t => KindOrder(KindOf(t.Project, t.Test)))
                    .ToList(),
                entry =>
                {
                    var (project, test) = entry;
                    var tag = classifications.GetValueOrDefault((project, test.ResultKey)) is { } c
                        ? $" [{Label(c.Kind)}{(c.Flakiness is { } evidence ? $": {evidence.Brief()}" : "")}]"
                        : "";
                    if (test.Attempts.Count > 0)
                    {
                        tag += $" (failed all {test.Attempts.Count + 1} attempts)";
                    }

                    var name = repeatedKeys.Contains(test.ResultKey) ? $"{project} :: {test.ResultKey}" : test.ResultKey;
                    return test.ErrorMessage is { } message ? $"{name}{tag}: {Cap(message)}" : name + tag;
                });
        }

        AppendList(sb, "Passed on retry", run.Suites
                .SelectMany(s => s.Tests.Select(t => (Project: s.Name, Test: t)))
                .Where(t => t.Test.Status == TestStatus.Passed && t.Test.Attempts.Any(a => a.Status == TestStatus.Failed))
                .ToList(),
            entry =>
            {
                var (project, test) = entry;
                var failed = test.Attempts.Where(a => a.Status == TestStatus.Failed).ToList();
                var text = $"{test.ResultKey}: passed on attempt {test.Attempts.Count + 1} after {failed.Count} failure(s)";
                if (classifications.GetValueOrDefault((project, test.ResultKey))?.Flakiness is { } evidence)
                {
                    text += $" [flaky: {evidence.Brief()}]";
                }

                return failed[^1].ErrorMessage is { } message ? $"{text}: {Cap(message)}" : text;
            });

        if (run.RetriesSkippedReason is { } skipped)
        {
            _ = sb.AppendLine($"Retries skipped: {skipped}");
        }

        if (result.Verdict is VerificationVerdict.Incomplete)
        {
            var problems = run.Projects.Where(p => p.Status != ProjectRunStatus.Completed || p.TestCount == 0).ToList();
            AppendList(sb, "Projects without complete results", problems,
                p => $"{p.ProjectPath}: {p.Status switch
                {
                    ProjectRunStatus.Completed => NoResults(p),
                    ProjectRunStatus.TimedOut when p.TimedOut is { } timeout => $"TimedOut ({timeout.Test} ran longer than {Durations.Format(timeout.Limit)})",
                    _ => p.Status.ToString(),
                }}");
            AppendList(sb, "Tests without complete results",
                tests.Where(t => t.Status is TestStatus.NotRun or TestStatus.Running).ToList(),
                t => $"{t.ResultKey}: {t.Status}");
            AppendList(sb, "Duplicate test IDs", run.DuplicateTestIds, id => id);
        }

        if (run.RunnerError is { } runnerError && result.Verdict != VerificationVerdict.Passed)
        {
            _ = sb.AppendLine($"Runner error: {Cap(runnerError)}");
        }

        _ = sb.Append($"Details: get_test_run(\"{run.RunId}\")");
        return sb.ToString();
    }

    /// <summary>"Failure history: 2 new, 1 pre-existing, 0 flaky, 3 unknown (...)".</summary>
    internal static string DescribeFailureHistory(IReadOnlyList<FailureClassification> classifications)
    {
        int Count(FailureKind kind) => classifications.Count(c => c.Kind == kind);
        return $"Failure history: {Count(FailureKind.New)} new, {Count(FailureKind.PreExisting)} pre-existing, " +
               $"{Count(FailureKind.Flaky)} flaky, {Count(FailureKind.Unknown)} unknown " +
               "(new = passed in the previous run of the test in this worktree; flaky = passed and failed on the same source, " +
               "in this or another worktree, or across retries in one run; " +
               "get_test_history(test) shows the evidence)";
    }

    internal static string Label(FailureKind kind) => kind switch
    {
        FailureKind.New => "new",
        FailureKind.PreExisting => "pre-existing",
        FailureKind.Flaky => "flaky",
        _ => "unknown history",
    };

    // New failures first: they are the ones the latest change most likely caused.
    private static int KindOrder(FailureKind? kind) => kind switch
    {
        FailureKind.New => 0,
        FailureKind.Unknown or null => 1,
        FailureKind.Flaky => 2,
        _ => 3,
    };

    private static string NoResults(TestRunProjectOutcome project) =>
        project.NoTestsReason is { } reason ? $"no test results ({Cap(reason)})" : "no test results";

    private static void AppendList<T>(StringBuilder sb, string title, IReadOnlyList<T> items, Func<T, string> describe,
        Func<string, string>? cap = null)
    {
        cap ??= Cap;
        if (items.Count == 0)
        {
            return;
        }

        _ = sb.AppendLine($"{title} ({items.Count}):");
        foreach (var item in items.Take(MaxListedItems))
        {
            _ = sb.AppendLine($"  - {cap(describe(item))}");
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

    // Build errors synthesized from raw output put the cause at the end, so keep both ends.
    private static string CapBuildError(string text)
    {
        var line = text.ReplaceLineEndings(" | ").Trim();
        if (line.Length <= MaxBuildErrorLength)
        {
            return line;
        }

        var tailLength = MaxBuildErrorLength - BuildErrorHeadLength;
        return line[..BuildErrorHeadLength] + " … " + line[^tailLength..];
    }
}
