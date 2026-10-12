// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Piston.Engine;
using Piston.Engine.Models;

namespace Piston.Mcp.Tools;

[McpServerToolType]
public sealed class TestTools(IEngine engine, IMcpCallRecorder recorder)
{
    [McpServerTool(Name = "run_tests"), Description("Force a full test run of the loaded solution. Returns a results summary plus the run ID, status, scope, freshness and source fingerprint; use GetTestRun with the run ID for per-test provenance.")]
    public async Task<string> RunTestsAsync(CancellationToken ct)
    {
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
            result = $"Phase: {state.Phase}, " +
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
            recorder.Record("RunTests", null, succeeded ? result! : null, sw.Elapsed.TotalMilliseconds, succeeded);
        }

        return result;
    }

    [McpServerTool(Name = "verify"), Description(
        "Get current full verification of the solution as it is on disk now. Reuses the latest run when it is a " +
        "completed full-scope, unfiltered result for the present source fingerprint; otherwise awaits an active " +
        "full run or forces one, retrying at most twice if the source changes during the run (then Stale). " +
        "Only 'Verdict: Passed' counts as full verification. Other verdicts: TestsFailed, BuildFailed, Incomplete, " +
        "Stale, Cancelled, NotFullScope (a test filter is set), Faulted, TimedOut. " +
        "Prefer this over run_tests and over get_test_results, which may combine several runs.")]
    public async Task<string> VerifyAsync(
        [Description("Overall timeout in seconds (default 600). On timeout the run continues; call verify again.")] int timeoutSeconds = 600,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string? logSummary = null;
        var succeeded = false;
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600));
            var verification = await engine.VerifyAsync(timeout, ct).ConfigureAwait(false);
            var result = VerificationReport.Format(verification);
            logSummary = $"Verdict: {verification.Verdict}; source: {verification.Source}; attempts: {verification.Attempts}";
            succeeded = true;
            return result;
        }
        finally
        {
            sw.Stop();
            recorder.Record("Verify", timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                logSummary, sw.Elapsed.TotalMilliseconds, succeeded);
        }
    }

    [McpServerTool, Description("Get current test results from the last test run.")]
    public string GetTestResults()
    {
        var sw = Stopwatch.StartNew();
        var state = engine.State;
        var sb = new StringBuilder();
        _ = sb.AppendLine($"Phase: {state.Phase}");
        _ = sb.AppendLine($"Passed: {state.TotalPassed}, Failed: {state.TotalFailed}, Skipped: {state.TotalSkipped}");
        _ = sb.AppendLine($"Tests: {state.CompletedTests}/{state.TotalExpectedTests}");
        _ = sb.AppendLine($"Latest {DescribeRun(state.LastRun, includeCounts: false)}");
        if (state.CurrentRun is { } active)
        {
            _ = sb.AppendLine($"Active {DescribeRun(active, includeCounts: false)}");
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
        recorder.Record(nameof(GetTestResults), null, $"{state.TotalPassed} passed, {state.TotalFailed} failed", sw.Elapsed.TotalMilliseconds, true);
        return result;
    }

    [McpServerTool, Description(
        "Get provenance and per-test results for exactly one test run as JSON (schema piston.test-run/v1): " +
        "run ID, status, full/selective scope, filters, freshness, source fingerprints captured before and after the run, " +
        "build/runner context, machine-wide execution admission (queue position and CPU/memory reservations), " +
        "and every test's fully-qualified name and status. " +
        "Pass a run ID, \"latest\" or empty for the most recent finished run, or \"active\" for the run in progress. " +
        "A result is trustworthy for a full-scope oracle only when isCurrentFullResult is true and sourceAfter matches the expected source.")]
    public string GetTestRun(string runId = "")
    {
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
            recorder.Record(nameof(GetTestRun), runId, message, sw.Elapsed.TotalMilliseconds, false);
            throw new McpException(message);
        }

        var result = TestRunJson.Serialize(run);
        sw.Stop();
        recorder.Record(nameof(GetTestRun), runId, $"{run.RunId} {run.Status}", sw.Elapsed.TotalMilliseconds, true);
        return result;
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

    [McpServerTool, Description("Set a test filter to narrow which tests are run. Pass empty string to clear.")]
    public string SetTestFilter(string filter)
    {
        var sw = Stopwatch.StartNew();
        var effectiveFilter = string.IsNullOrEmpty(filter) ? null : filter;
        engine.SetFilter(effectiveFilter);
        var result = $"Filter set to: {effectiveFilter ?? "(none)"}";
        sw.Stop();
        recorder.Record(nameof(SetTestFilter), filter, result, sw.Elapsed.TotalMilliseconds, true);
        return result;
    }

    [McpServerTool, Description("Clear current test results and persisted coverage in the current solution directory. A running test run may publish new results afterwards.")]
    public string ClearResults()
    {
        var sw = Stopwatch.StartNew();
        engine.ClearResults();
        const string Result = "Results cleared.";
        sw.Stop();
        recorder.Record(nameof(ClearResults), null, Result, sw.Elapsed.TotalMilliseconds, true);
        return Result;
    }
}
