using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;
using Piston.Engine;
using Piston.Engine.Models;

namespace Piston.Mcp.Tools;

[McpServerToolType]
public sealed class TestTools(IEngine engine, IMcpCallRecorder recorder)
{
    [McpServerTool, Description("Force a full test run of the loaded solution. Returns test results summary.")]
    public async Task<string> RunTests(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string result = string.Empty;
        var succeeded = true;
        try
        {
            var timeout = TimeSpan.FromMinutes(10);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await engine.ForceRunAsync().WaitAsync(cts.Token).ConfigureAwait(false);

            var state = engine.State;
            result = $"Phase: {state.Phase}, " +
                     $"Passed: {state.TotalPassed}, Failed: {state.TotalFailed}, Skipped: {state.TotalSkipped}, " +
                     $"Tests: {state.CompletedTests}/{state.TotalExpectedTests}, " +
                     $"Suites: {state.TestSuites.Count}";
        }
        catch
        {
            succeeded = false;
            throw;
        }
        finally
        {
            sw.Stop();
            recorder.Record(nameof(RunTests), null, succeeded ? result! : null, sw.Elapsed.TotalMilliseconds, succeeded);
        }

        return result;
    }

    [McpServerTool, Description("Get current test results from the last test run.")]
    public string GetTestResults()
    {
        var sw = Stopwatch.StartNew();
        var state = engine.State;
        var sb = new StringBuilder();
        sb.AppendLine($"Phase: {state.Phase}");
        sb.AppendLine($"Passed: {state.TotalPassed}, Failed: {state.TotalFailed}, Skipped: {state.TotalSkipped}");
        sb.AppendLine($"Tests: {state.CompletedTests}/{state.TotalExpectedTests}");

        foreach (var suite in state.TestSuites)
        {
            sb.AppendLine($"\nSuite: {suite.Name}");
            foreach (var test in suite.Tests)
            {
                sb.Append($"  [{test.Status}] {test.DisplayName}");
                if (test.Duration.TotalMilliseconds > 0)
                    sb.Append($" ({test.Duration.TotalMilliseconds:F0}ms)");
                if (test.ErrorMessage is not null)
                    sb.Append($" - {test.ErrorMessage}");
                sb.AppendLine();
            }
        }

        var result = sb.ToString();
        sw.Stop();
        recorder.Record(nameof(GetTestResults), null, $"{state.TotalPassed} passed, {state.TotalFailed} failed", sw.Elapsed.TotalMilliseconds, true);
        return result;
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

    [McpServerTool, Description("Clear all test results and coverage data.")]
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
