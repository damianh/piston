// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Piston.Engine;
using Piston.Engine.Execution;
using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Mcp.Tools;

[McpServerToolType]
public sealed class TestTools(IEngine engine, IMcpCallRecorder recorder, EngineAvailability? availability = null)
{
    public const string RunTestsTool = "run_tests";
    public const string VerifyTool = "verify";
    public const string GetTestResultsTool = "get_test_results";
    public const string GetTestRunTool = "get_test_run";
    public const string SetTestFilterTool = "set_test_filter";
    public const string ClearResultsTool = "clear_results";

    private const string ExpectedSolutionDescription =
        "Optional solution file or worktree directory you expect this Piston to serve. " +
        "When set and it does not match, the tool fails instead of reporting another worktree's results.";

    [McpServerTool(Name = RunTestsTool), Description("Force a full test run of the loaded solution. Returns the solution path and a results summary plus the run ID, status, scope, freshness and source fingerprint; use get_test_run with the run ID for per-test provenance.")]
    public async Task<string> RunTestsAsync(
        [Description(ExpectedSolutionDescription)] string? expectedSolution = null,
        CancellationToken ct = default)
    {
        var solution = GuardSolution(expectedSolution);
        var sw = Stopwatch.StartNew();
        var result = string.Empty;
        var succeeded = true;
        try
        {
            var timeout = TimeSpan.FromMinutes(10);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var run = await engine.RequestRunAsync().WaitAsync(cts.Token).ConfigureAwait(false);

            var state = engine.State;
            result = $"Solution: {solution}" + Environment.NewLine +
                     $"Phase: {state.Phase}, " +
                     $"Passed: {state.TotalPassed}, Failed: {state.TotalFailed}, Skipped: {state.TotalSkipped}, " +
                     $"Tests: {state.CompletedTests}/{state.TotalExpectedTests}, " +
                     $"Suites: {state.TestSuites.Count}" +
                     Environment.NewLine + DescribeRun(run, includeCounts: true);
            if (run is not null)
            {
                result += Environment.NewLine + $"Use GetTestRun(\"{run.RunId}\") for per-test results bound to this run.";
            }
        }
        catch
        {
            succeeded = false;
            throw;
        }
        finally
        {
            sw.Stop();
            recorder.Record(RunTestsTool, null, succeeded ? result! : null, sw.Elapsed.TotalMilliseconds, succeeded);
        }

        return result;
    }

    [McpServerTool(Name = VerifyTool), Description(
        "Get current full verification of the solution as it is on disk now. Reuses the latest run when it is a " +
        "completed full-scope, unfiltered result for the present source fingerprint; otherwise awaits an active " +
        "full run or forces one, retrying at most twice if the source changes during the run (then Stale). " +
        "Only 'Verdict: Passed' counts as full verification. Other verdicts: TestsFailed, BuildFailed, Incomplete, " +
        "Stale, Cancelled, NotFullScope (a test filter is set), Faulted, TimedOut. " +
        "Prefer this over run_tests and over get_test_results, which may combine several runs. " +
        "The Solution line names the solution verified. If the tool fails because Piston is owned by another process, " +
        "the worktree was deleted, or Piston could not start, fall back to `dotnet test`.")]
    public async Task<string> VerifyAsync(
        [Description("Overall timeout in seconds (default 600). On timeout the run continues; call verify again.")] int timeoutSeconds = 600,
        [Description(ExpectedSolutionDescription)] string? expectedSolution = null,
        CancellationToken ct = default)
    {
        var solution = GuardSolution(expectedSolution);
        var sw = Stopwatch.StartNew();
        string? logSummary = null;
        var succeeded = false;
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600));
            var verification = await engine.VerifyAsync(timeout, ct).ConfigureAwait(false);
            var result = VerificationReport.Format(verification, solution);
            logSummary = $"Verdict: {verification.Verdict}; source: {verification.Source}; attempts: {verification.Attempts}";
            succeeded = true;
            return result;
        }
        finally
        {
            sw.Stop();
            recorder.Record(VerifyTool, timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                logSummary, sw.Elapsed.TotalMilliseconds, succeeded);
        }
    }

    [McpServerTool(Name = GetTestResultsTool), Description("Get current test results from the last test run.")]
    public string GetTestResults()
    {
        availability?.ThrowIfUnavailable();
        var sw = Stopwatch.StartNew();
        var state = engine.State;
        var sb = new StringBuilder();
        _ = sb.AppendLine($"Solution: {state.SolutionPath ?? "(unknown)"}");
        _ = sb.AppendLine($"Phase: {state.Phase}");
        _ = sb.AppendLine($"Passed: {state.TotalPassed}, Failed: {state.TotalFailed}, Skipped: {state.TotalSkipped}");
        _ = sb.AppendLine($"Tests: {state.CompletedTests}/{state.TotalExpectedTests}");
        _ = sb.AppendLine($"Latest {DescribeRun(state.LastRun, includeCounts: false)}");
        if (state.CurrentRun is { } active)
        {
            _ = sb.AppendLine($"Active {DescribeRun(active, includeCounts: false)}");
        }

        if (state.LongestRunningTest is { } running)
        {
            var elapsed = Durations.Format(DateTimeOffset.UtcNow - running.StartedAt);
            _ = sb.AppendLine($"Longest running: {running.Name} ({elapsed}{(running.PossiblyHanging ? ", possibly hanging" : "")})");
        }

        _ = sb.AppendLine("Note: these aggregate results may combine several runs; use GetTestRun for run-bound provenance.");

        foreach (var suite in state.TestSuites)
        {
            _ = sb.AppendLine($"\nSuite: {suite.Name}" + (suite.RunId is null ? "" : $" (run {suite.RunId})"));
            foreach (var test in suite.Tests)
            {
                _ = sb.Append($"  [{test.Status}] {test.DisplayName}");
                if (test.Duration.TotalMilliseconds > 0)
                {
                    _ = sb.Append($" ({test.Duration.TotalMilliseconds:F0}ms)");
                }

                if (test.ErrorMessage is not null)
                {
                    _ = sb.Append($" - {test.ErrorMessage}");
                }

                _ = sb.AppendLine();
            }
        }

        var result = sb.ToString();
        sw.Stop();
        recorder.Record(GetTestResultsTool, null, $"{state.TotalPassed} passed, {state.TotalFailed} failed", sw.Elapsed.TotalMilliseconds, true);
        return result;
    }

    [McpServerTool(Name = GetTestRunTool), Description(
        "Get provenance and per-test results for exactly one test run as JSON (schema piston.test-run/v1): " +
        "run ID, status, full/selective scope, filters, freshness, source fingerprints captured before and after the run, " +
        "build/runner context, machine-wide execution admission (queue position and CPU/memory reservations), " +
        "and every test's fully-qualified name and status. " +
        "Pass a run ID, \"latest\" or empty for the most recent finished run, or \"active\" for the run in progress. " +
        "A result is trustworthy for a full-scope oracle only when isCurrentFullResult is true and sourceAfter matches the expected source.")]
    public string GetTestRun(string runId = "")
    {
        availability?.ThrowIfUnavailable();
        var sw = Stopwatch.StartNew();
        var state = engine.State;
        var key = runId?.Trim() ?? "";
        var run = key switch
        {
            "" or "latest" => state.LastRun,
            "active" => state.CurrentRun,
            _ => state.FindRun(key),
        };

        if (run is null)
        {
            var message = key switch
            {
                "" or "latest" => "No test run has finished yet.",
                "active" => "No test run is in progress.",
                _ => $"Test run '{key}' was not found; only the {PistonState.MaxRecentRuns} most recent runs are retained in memory.",
            };
            sw.Stop();
            recorder.Record(GetTestRunTool, runId, message, sw.Elapsed.TotalMilliseconds, false);
            throw new McpException(message);
        }

        var result = TestRunJson.Serialize(run);
        sw.Stop();
        recorder.Record(GetTestRunTool, runId, $"{run.RunId} {run.Status}", sw.Elapsed.TotalMilliseconds, true);
        return result;
    }

    private string GuardSolution(string? expectedSolution)
    {
        availability?.ThrowIfUnavailable();
        var actual = engine.State.SolutionPath;
        if (!string.IsNullOrWhiteSpace(expectedSolution) && !SolutionMatches(actual, TryGetWorktreeRoot(actual), expectedSolution))
        {
            throw new McpException(
                $"Solution mismatch: this Piston serves '{actual ?? "(none)"}', not '{expectedSolution}'. " +
                $"Use the Piston MCP server started in that worktree, or {EngineAvailability.Fallback}");
        }

        return actual ?? "(unknown)";
    }

    private static string? TryGetWorktreeRoot(string? solutionPath)
    {
        if (solutionPath is null)
        {
            return null;
        }

        try
        {
            return WorktreeIdentity.FromSolutionPath(solutionPath).RootPath;
        }
        catch (Exception ex) when (ex is ExecutionCoordinationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="expected"/> is the solution itself, its directory, or the root of its worktree.
    /// Ancestors of the worktree root do not match, so a parent repository never matches a nested worktree.
    /// </summary>
    internal static bool SolutionMatches(string? actual, string? worktreeRoot, string expected)
    {
        if (actual is null)
        {
            return false;
        }

        var comparison = StringComparison.Ordinal;
        var e = Normalize(expected.Trim());
        var a = Normalize(actual);
        return string.Equals(a, e, comparison) ||
               (Path.GetDirectoryName(a) is { } dir && string.Equals(Normalize(dir), e, comparison)) ||
               (worktreeRoot is not null && string.Equals(Normalize(worktreeRoot), e, comparison));

        static string Normalize(string path) => WorktreeIdentity.CanonicalizePath(path);
    }

    private static string DescribeRun(TestRunRecord? run, bool includeCounts)
    {
        if (run is null)
        {
            return "Run: (none)";
        }

        var fp = run.SourceAfter ?? run.SourceBefore;
        var source = fp is null ? "unknown" : $"{fp.Algorithm}:{fp.Value}";
        var text = $"Run: {run.RunId} | Status: {run.Status} | Scope: {run.Scope?.ToString() ?? "Unknown"} | " +
                   $"Filtered: {(run.IsFiltered ? "yes" : "no")} | Freshness: {run.Freshness} | " +
                   $"Source: {source}";
        if (run.Admission is { } admission)
        {
            text += " | " + DescribeAdmission(admission);
        }

        if (includeCounts)
        {
            var tests = run.Tests.ToList();
            text += $" | Run tests: {tests.Count} (Passed: {tests.Count(t => t.Status == TestStatus.Passed)}, " +
                    $"Failed: {tests.Count(t => t.Status == TestStatus.Failed)}, " +
                    $"Skipped: {tests.Count(t => t.Status == TestStatus.Skipped)})";
        }
        return text;
    }

    internal static string DescribeAdmission(TestRunAdmission a)
    {
        var memory = a.BudgetMemoryMb is { } budgetMb
            ? $", memory {a.MemoryMbInUse}+{a.ReservedMemoryMb ?? 0}/{budgetMb} MiB"
            : a.ReservedMemoryMb is { } reservedMb
                ? $", memory {a.MemoryMbInUse}+{reservedMb} MiB (unbudgeted)"
                : "";
        var queue = a.State == TestRunAdmissionState.Queued
            ? $"Queued ({a.QueuedAhead} ahead, {a.QueueLength} waiting)"
            : "Admitted";
        return $"Admission: {queue}; machine budget CPU slots {a.CpuSlotsInUse}+{a.ReservedCpuSlots}/{a.BudgetCpuSlots}{memory} ({a.BudgetSource})";
    }

    [McpServerTool(Name = SetTestFilterTool), Description("Set a test filter to narrow which tests are run. Pass empty string to clear.")]
    public string SetTestFilter(string filter)
    {
        availability?.ThrowIfUnavailable();
        var sw = Stopwatch.StartNew();
        var effectiveFilter = string.IsNullOrEmpty(filter) ? null : filter;
        engine.SetFilter(effectiveFilter);
        var result = $"Filter set to: {effectiveFilter ?? "(none)"}";
        sw.Stop();
        recorder.Record(SetTestFilterTool, filter, result, sw.Elapsed.TotalMilliseconds, true);
        return result;
    }

    [McpServerTool(Name = ClearResultsTool), Description("Clear current test results and persisted coverage in the current solution directory. A running test run may publish new results afterwards.")]
    public string ClearResults()
    {
        availability?.ThrowIfUnavailable();
        var sw = Stopwatch.StartNew();
        engine.ClearResults();
        const string Result = "Results cleared.";
        sw.Stop();
        recorder.Record(ClearResultsTool, null, Result, sw.Elapsed.TotalMilliseconds, true);
        return Result;
    }
}
