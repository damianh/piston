using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Piston.Engine;
using Piston.Engine.Models;

namespace Piston.Mcp.Tools;

[McpServerToolType]
public static class TestTools
{
    [McpServerTool, Description("Force a full test run of the loaded solution. Returns test results summary.")]
    public static async Task<string> RunTests(IEngine engine, CancellationToken ct)
    {
        await engine.ForceRunAsync().ConfigureAwait(false);

        var timeout = TimeSpan.FromMinutes(10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (engine.State.Phase != PistonPhase.Idle && engine.State.Phase != PistonPhase.Error)
        {
            await Task.Delay(500, cts.Token).ConfigureAwait(false);
        }

        var state = engine.State;
        return $"Phase: {state.Phase}, " +
               $"Passed: {state.TotalPassed}, Failed: {state.TotalFailed}, Skipped: {state.TotalSkipped}, " +
               $"Tests: {state.CompletedTests}/{state.TotalExpectedTests}, " +
               $"Suites: {state.TestSuites.Count}";
    }

    [McpServerTool, Description("Get current test results from the last test run.")]
    public static string GetTestResults(IEngine engine)
    {
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
                {
                    sb.Append($" ({test.Duration.TotalMilliseconds:F0}ms)");
                }
                if (test.ErrorMessage is not null)
                {
                    sb.Append($" - {test.ErrorMessage}");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    [McpServerTool, Description("Set a test filter to narrow which tests are run. Pass empty string to clear.")]
    public static string SetTestFilter(IEngine engine, string filter)
    {
        var effectiveFilter = string.IsNullOrEmpty(filter) ? null : filter;
        engine.SetFilter(effectiveFilter);
        return $"Filter set to: {effectiveFilter ?? "(none)"}";
    }

    [McpServerTool, Description("Clear all test results and coverage data.")]
    public static string ClearResults(IEngine engine)
    {
        engine.ClearResults();
        return "Results cleared.";
    }
}
