// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Piston.Engine.Impact;
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

    private const string ListTestsHeader = "The following Tests are available:";

    /// <summary>Minimum interval between live progress callbacks.</summary>
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Lists the tests in <paramref name="projectPath"/> via <c>dotnet test --no-build --list-tests</c>.
    /// Returns an empty list on failure, timeout, or cancellation — discovery is best-effort.
    /// </summary>
    internal static Task<IReadOnlyList<TestResult>> DiscoverAsync(
        string projectPath,
        string? filter,
        Action<IReadOnlyList<TestResult>>? onPartial,
        CancellationToken ct) =>
        TestListProcess.RunAsync(
            $"test \"{projectPath}\" --no-build --list-tests" + FilterArgument(filter),
            workingDirectory: null,
            projectPath,
            "VSTestRunner",
            new TestListParser(IsListHeader),
            onPartial,
            ct);

    /// <summary>The <c>--filter</c> argument for <paramref name="filter"/>, or empty when there is none.</summary>
    internal static string FilterArgument(string? filter) =>
        string.IsNullOrWhiteSpace(filter) ? string.Empty : $" --filter \"{filter}\"";

    private static bool IsListHeader(string line) =>
        line.Trim().Equals(ListTestsHeader, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses the test names printed after VSTest's "The following Tests are available:" header.</summary>
    internal static IReadOnlyList<TestResult> ParseListTestsOutput(IEnumerable<string> lines) =>
        TestListParser.ParseAll(new TestListParser(IsListHeader), lines);

    /// <summary>Last dotted segment of a test name, ignoring dots inside any argument list.</summary>
    internal static string DisplayNameOf(string fqn)
    {
        var paren = fqn.IndexOf('(');
        var head = paren >= 0 ? fqn[..paren] : fqn;
        var dot = head.LastIndexOf('.');
        return dot >= 0 ? fqn[(dot + 1)..] : fqn;
    }

    internal static async Task<ProjectTestResult> RunAsync(
        string projectPath,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        ITestResultParser parser,
        CancellationToken ct)
    {
        var resultsDir = Path.Combine(Path.GetTempPath(), $"piston-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(resultsDir);

        try
        {
            var args = $"test \"{projectPath}\" --no-build --verbosity normal " +
                       $"--logger \"trx;LogFileName=piston-results.trx\" " +
                       $"--results-directory \"{resultsDir}\"";

            if (collectCoverage)
            {
                args += " --collect \"XPlat Code Coverage\"";
            }

            args += FilterArgument(filter);

            var liveResults = new LiveResultTracker();

            using var progress = onProgress is null
                ? null
                : new TrailingThrottle(() => onProgress(liveResults.Snapshot()), ProgressInterval);

            // Per-test start/end events from the PistonLive data collector, when it is deployed.
            var collectorDir = LiveTestEventListener.CollectorDirectory;
            await using var liveEvents = onProgress is null || collectorDir is null
                ? null
                : LiveTestEventListener.TryStart(evt =>
                {
                    if (liveResults.ApplyCollectorEvent(evt))
                    {
                        progress?.Signal();
                    }
                });

            if (liveEvents is not null)
            {
                args += $" --collect \"{LiveTestEventListener.CollectorFriendlyName}\" --test-adapter-path \"{collectorDir}\"";
            }

            var log = DiagnosticLog.Instance;
            log?.Write("VSTestRunner", $"Project: {projectPath}");
            log?.Write("VSTestRunner", $"Args: dotnet {args}");

            var psi = new ProcessStartInfo("dotnet", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            MsBuildLocatorGuard.RestoreOriginalEnvironment(psi);
            if (liveEvents is not null)
            {
                psi.Environment[LiveTestEventListener.PortVariable] = liveEvents.Port.ToString(CultureInfo.InvariantCulture);
            }

            // Collect stderr lines for error surfacing
            var stderrLines = new System.Collections.Concurrent.ConcurrentBag<string>();

            using var process = new Process { StartInfo = psi };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                var m = ResultLineRegex.Match(e.Data);
                if (!m.Success)
                {
                    return;
                }

                var outcomeStr = m.Groups[1].Value;
                var displayName = m.Groups[2].Value.Trim();

                var status = outcomeStr.ToLowerInvariant() switch
                {
                    "passed" => TestStatus.Passed,
                    "failed" => TestStatus.Failed,
                    "skipped" => TestStatus.Skipped,
                    _ => TestStatus.NotRun,
                };

                if (liveResults.ApplyConsoleResult(displayName, status))
                {
                    progress?.Signal();
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null && e.Data.Trim().Length > 0)
                {
                    log?.Write("VSTestRunner:stderr", e.Data.Trim());
                    stderrLines.Add(e.Data.Trim());
                }
            };

            _ = process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                finally
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                log?.Write("VSTestRunner", "Cancelled — returning empty result");
                return new ProjectTestResult(projectPath, [], null, [], Crashed: false);
            }

            log?.Write("VSTestRunner", $"ExitCode: {process.ExitCode}");

            // Deliver any buffered collector events, then one final progress update.
            if (liveEvents is not null)
            {
                await liveEvents.DisposeAsync().ConfigureAwait(false);
            }

            progress?.Flush();

            // Parse TRX files for authoritative results (durations, error messages, stack traces)
            var trxFiles = Directory.GetFiles(resultsDir, "*.trx", SearchOption.AllDirectories);
            log?.Write("VSTestRunner", $"TRX files found: {trxFiles.Length}");

            var suites = new List<TestSuite>();
            foreach (var trx in trxFiles)
            {
                try { suites.AddRange(parser.Parse(trx)); }
                catch { /* malformed TRX — skip */ }
            }

            // Name per-project suites after the project so live, discovered and final results line up.
            if (TestProjectNames.IsProjectFile(projectPath))
            {
                var projectSuiteName = TestProjectNames.SuiteName(projectPath);
                suites = [.. suites.Select(s => s with { Name = projectSuiteName })];
            }

            // Surface stderr when there are no results (likely a runner-level failure)
            string? runnerError = null;
            if (suites.Count == 0 && !stderrLines.IsEmpty)
            {
                runnerError = string.Join(Environment.NewLine, stderrLines.OrderBy(x => x));
                log?.Write("VSTestRunner", $"RunnerError: {runnerError}");
            }

            var totalTests = suites.SelectMany(s => s.Tests).Count();
            log?.Write("VSTestRunner",
                $"Result: {totalTests} test(s) in {suites.Count} suite(s)");

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
}

/// <summary>
/// Live per-test results of one <c>dotnet test</c> run, keyed by <see cref="TestIdentity"/> so they line up
/// with seeded and final (TRX) results. Collector events carry the identity; console result lines only carry
/// the display name, which is mapped to the identity the collector reported for it. Display names need not
/// be unique (the same method name in two classes), so an ambiguous console line is left to the collector.
/// </summary>
internal sealed class LiveResultTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, TestStatus> _results = new(StringComparer.Ordinal);

    // Display name -> identity reported by the collector; null when several tests share the display name.
    private readonly Dictionary<string, string?> _identityByDisplayName = new(StringComparer.Ordinal);

    /// <summary>Applies a collector event; returns <see langword="true"/> when the results changed.</summary>
    public bool ApplyCollectorEvent(LiveTestEvent evt)
    {
        var identity = evt.Identity;
        lock (_lock)
        {
            if (!_identityByDisplayName.TryGetValue(evt.DisplayName, out var known))
            {
                _identityByDisplayName[evt.DisplayName] = identity;

                // A console line that arrived before the collector named its test is keyed by display name.
                if (identity != evt.DisplayName && _results.Remove(evt.DisplayName, out var early))
                {
                    _results[identity] = early;
                }
            }
            else if (known is not null && known != identity)
            {
                _identityByDisplayName[evt.DisplayName] = null;
            }

            var exists = _results.TryGetValue(identity, out var current);
            // Console result lines are authoritative; the collector only fills gaps.
            if (evt.Status == TestStatus.Running ? exists : exists && current != TestStatus.Running)
            {
                return false;
            }

            _results[identity] = evt.Status;
            return true;
        }
    }

    /// <summary>Applies a console result line; returns <see langword="true"/> when the results changed.</summary>
    public bool ApplyConsoleResult(string displayName, TestStatus status)
    {
        lock (_lock)
        {
            string key;
            if (_identityByDisplayName.TryGetValue(displayName, out var identity))
            {
                if (identity is null)
                {
                    return false;
                }

                key = identity;
            }
            else
            {
                // No collector event for it (yet, or no collector at all): the display name is all there is.
                key = displayName;
            }

            _results[key] = status;
            return true;
        }
    }

    /// <summary>The live results so far, as a single suite.</summary>
    public IReadOnlyList<TestSuite> Snapshot()
    {
        List<KeyValuePair<string, TestStatus>> snapshot;
        lock (_lock)
        {
            snapshot = [.. _results];
        }

        if (snapshot.Count == 0)
        {
            return [];
        }

        var results = snapshot
            .Select(kvp => new TestResult(
                FullyQualifiedName: kvp.Key,
                DisplayName: TestProcessRunner.DisplayNameOf(kvp.Key),
                Status: kvp.Value,
                Duration: TimeSpan.Zero,
                Output: null,
                ErrorMessage: null,
                StackTrace: null,
                Source: null))
            .ToList();

        return [new TestSuite("…", results, DateTimeOffset.UtcNow, TimeSpan.Zero)];
    }
}
