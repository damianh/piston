using System.Diagnostics;
using System.Text.RegularExpressions;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Internal helper that spawns a single <c>dotnet test</c> process, streams live progress,
/// parses TRX results, and collects Cobertura coverage paths.
/// Shared between <see cref="TestRunnerService"/> and <see cref="TestProcessPool"/>.
/// </summary>
internal static class TestProcessRunner
{
    // Matches dotnet test --verbosity normal output lines such as:
    //   "  Passed Namespace.Class.Method [7 ms]"
    //   "  Failed Namespace.Class.Method [< 1 ms]"
    //   "  Skipped Namespace.Class.Method"
    private static readonly Regex ResultLineRegex = new(
        @"^\s+(Passed|Failed|Skipped|not run)\s+(.+?)(?:\s+\[.*?\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static async Task<ProjectTestResult> RunAsync(
        string projectPath,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        ITestResultParser parser,
        CancellationToken ct)
    {
        var resultsDir = Path.Combine(Path.GetTempPath(), $"piston-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resultsDir);

        try
        {
            var args = $"test \"{projectPath}\" --no-build --verbosity normal " +
                       $"--logger \"trx;LogFileName=piston-results.trx\" " +
                       $"--results-directory \"{resultsDir}\"";

            if (collectCoverage)
                args += " --collect \"XPlat Code Coverage\"";

            if (!string.IsNullOrWhiteSpace(filter))
                args += $" --filter \"{filter}\"";

            var psi = new ProcessStartInfo("dotnet", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            // Live progress state: FQN → current status (starts as Running when first seen)
            var liveResults = new Dictionary<string, (TestStatus Status, string DisplayName)>(
                StringComparer.Ordinal);
            var liveResultsLock = new object();

            // Collect stderr lines for error surfacing
            var stderrLines = new System.Collections.Concurrent.ConcurrentBag<string>();

            // Throttle: fire onProgress at most every 150ms
            var lastProgressFire = DateTimeOffset.MinValue;
            const int progressThrottleMs = 150;

            using var process = new Process { StartInfo = psi };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var m = ResultLineRegex.Match(e.Data);
                if (!m.Success) return;

                var outcomeStr = m.Groups[1].Value;
                var fqn        = m.Groups[2].Value.Trim();

                var status = outcomeStr.ToLowerInvariant() switch
                {
                    "passed"  => TestStatus.Passed,
                    "failed"  => TestStatus.Failed,
                    "skipped" => TestStatus.Skipped,
                    _         => TestStatus.NotRun,
                };

                // Use last segment of FQN as display name
                var dot = fqn.LastIndexOf('.');
                var displayName = dot >= 0 ? fqn[(dot + 1)..] : fqn;

                lock (liveResultsLock)
                {
                    liveResults[fqn] = (status, displayName);
                }

                if (onProgress is null) return;

                var now = DateTimeOffset.UtcNow;
                bool shouldFire;
                lock (liveResultsLock)
                {
                    shouldFire = (now - lastProgressFire).TotalMilliseconds >= progressThrottleMs;
                    if (shouldFire) lastProgressFire = now;
                }

                if (shouldFire)
                    onProgress(BuildLiveSnapshot(liveResults, liveResultsLock));
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null && e.Data.Trim().Length > 0)
                    stderrLines.Add(e.Data.Trim());
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new ProjectTestResult(projectPath, [], null, [], Crashed: false);
            }

            // Fire one final progress update with everything that arrived
            onProgress?.Invoke(BuildLiveSnapshot(liveResults, liveResultsLock));

            // Parse TRX files for authoritative results (durations, error messages, stack traces)
            var trxFiles = Directory.GetFiles(resultsDir, "*.trx", SearchOption.AllDirectories);

            var suites = new List<TestSuite>();
            foreach (var trx in trxFiles)
            {
                try { suites.AddRange(parser.Parse(trx)); }
                catch { /* malformed TRX — skip */ }
            }

            // Surface stderr when there are no results (likely a runner-level failure)
            string? runnerError = null;
            if (suites.Count == 0 && !stderrLines.IsEmpty)
                runnerError = string.Join(Environment.NewLine, stderrLines.OrderBy(x => x));

            // Glob for Cobertura XML files if coverage was collected
            IReadOnlyList<string> coverageReportPaths = [];
            if (collectCoverage)
            {
                coverageReportPaths = Directory.GetFiles(
                    resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories);
            }

            return new ProjectTestResult(projectPath, suites, runnerError, coverageReportPaths, Crashed: false);
        }
        finally
        {
            // Only clean up immediately when we are not returning coverage paths to the caller.
            // When coverage is enabled and paths were found, the caller cleans up the results dir.
            if (!collectCoverage)
            {
                try { Directory.Delete(resultsDir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Converts the current live dictionary into a list of <see cref="TestSuite"/> objects
    /// grouped by namespace.
    /// </summary>
    private static IReadOnlyList<TestSuite> BuildLiveSnapshot(
        Dictionary<string, (TestStatus Status, string DisplayName)> liveResults,
        object lockObj)
    {
        List<KeyValuePair<string, (TestStatus Status, string DisplayName)>> snapshot;
        lock (lockObj)
        {
            snapshot = [.. liveResults];
        }

        if (snapshot.Count == 0) return [];

        var results = snapshot
            .Select(kvp => new TestResult(
                FullyQualifiedName: kvp.Key,
                DisplayName:        kvp.Value.DisplayName,
                Status:             kvp.Value.Status,
                Duration:           TimeSpan.Zero,
                Output:             null,
                ErrorMessage:       null,
                StackTrace:         null,
                Source:             null))
            .ToList();

        return [new TestSuite("…", results, DateTimeOffset.UtcNow, TimeSpan.Zero)];
    }
}
