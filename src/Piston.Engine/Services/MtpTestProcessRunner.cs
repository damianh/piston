// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Piston.Engine.Impact;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Internal helper that runs an MTP v2 test project via <c>dotnet test</c> in
/// Microsoft.Testing.Platform mode (<c>--project &lt;csproj&gt; --no-build --output Detailed</c>),
/// streams live progress via <see cref="MtpStdoutParser"/>, and builds authoritative
/// results from the parsed stdout output.
/// This class is completely separate from <see cref="TestProcessRunner"/>; the VSTest
/// code path is never modified.
/// </summary>
/// <remarks>
/// MTP mode of <c>dotnet test</c> requires a <c>global.json</c> with
/// <c>"test": { "runner": "Microsoft.Testing.Platform" }</c> in the solution root.
/// The <paramref name="solutionDirectory"/> is set as the working directory so the
/// SDK discovers the <c>global.json</c>.
/// </remarks>
internal static partial class MtpTestProcessRunner
{
    /// <summary>
    /// Lists the tests in an MTP project via <c>dotnet test --project &lt;csproj&gt; --no-build --list-tests</c>,
    /// the same entry point used to run it, so listed names match the names reported in run output.
    /// Returns an empty list on failure, timeout, or cancellation — discovery is best-effort.
    /// </summary>
    internal static Task<IReadOnlyList<TestResult>> DiscoverAsync(
        string projectPath,
        string solutionDirectory,
        CancellationToken ct) => TestListProcess.RunAsync(
            $"test --project \"{projectPath}\" --no-build --list-tests",
            solutionDirectory,
            projectPath,
            "MtpRunner",
            ParseListTestsOutput,
            ct);

    /// <summary>
    /// Parses MTP <c>--list-tests</c> output: each "Discovered N tests in assembly - &lt;path&gt;" header is
    /// followed by one indented test name per line, ending at the first blank or unindented line.
    /// </summary>
    internal static IReadOnlyList<TestResult> ParseListTestsOutput(IEnumerable<string> lines)
    {
        var results = new List<TestResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inList = false;

        foreach (var line in lines)
        {
            if (ListAssemblyHeaderRegex().IsMatch(line))
            {
                inList = true;
                continue;
            }

            if (!inList)
            {
                continue;
            }

            if (line.Length == 0 || !char.IsWhiteSpace(line[0]) || line.Trim().Length == 0)
            {
                inList = false;
                continue;
            }

            var name = line.Trim();
            if (seen.Add(name))
            {
                results.Add(new TestResult(name, TestProcessRunner.DisplayNameOf(name), TestStatus.NotRun,
                    TimeSpan.Zero, null, null, null, null));
            }
        }

        return results;
    }

    [GeneratedRegex(@"^Discovered \d+ tests? in assembly\b", RegexOptions.IgnoreCase)]
    private static partial Regex ListAssemblyHeaderRegex();

    internal static async Task<ProjectTestResult> RunAsync(
        string projectPath,
        string solutionDirectory,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct)
    {
        // Build CLI args: dotnet test --project <csproj> --no-build --output Detailed
        var args = $"test --project \"{projectPath}\" --no-build --output Detailed";

        if (!string.IsNullOrWhiteSpace(filter))
        {
            args += $" --filter \"{filter}\"";
        }

        var log = DiagnosticLog.Instance;
        log?.Write("MtpRunner", $"Project: {projectPath}");
        log?.Write("MtpRunner", $"WorkDir: {solutionDirectory}");
        log?.Write("MtpRunner", $"Args: dotnet {args}");

        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = solutionDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        MsBuildLocatorGuard.RestoreOriginalEnvironment(psi);

        var parser = new MtpStdoutParser();
        var liveResults = new Dictionary<string, (TestStatus Status, string DisplayName, TimeSpan Duration, string? ErrorMessage, string? StackTrace, string? Source)>(StringComparer.Ordinal);
        var liveResultsLock = new object();
        var stderrLines = new System.Collections.Concurrent.ConcurrentBag<string>();

        var lastProgressFire = DateTimeOffset.MinValue;
        const int progressThrottleMs = 150;

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            log?.Write("MtpRunner:stdout", e.Data);

            MtpParsedResult? parsed;
            lock (liveResultsLock)
            {
                parsed = parser.ProcessLine(e.Data);
            }

            if (parsed is null)
            {
                return;
            }

            RecordResult(parsed, liveResults, liveResultsLock);
            FireThrottledProgress(onProgress, liveResults, liveResultsLock,
                ref lastProgressFire, progressThrottleMs);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && e.Data.Trim().Length > 0)
            {
                log?.Write("MtpRunner:stderr", e.Data.Trim());
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
            log?.Write("MtpRunner", "Cancelled — returning empty result");
            return new ProjectTestResult(projectPath, [], null, [], Crashed: false);
        }

        log?.Write("MtpRunner", $"ExitCode: {process.ExitCode}");

        // Flush the last pending parsed result
        MtpParsedResult? last;
        lock (liveResultsLock)
        {
            last = parser.Flush();
        }
        if (last is not null)
        {
            RecordResult(last, liveResults, liveResultsLock);
        }

        // Fire final progress update
        onProgress?.Invoke(BuildLiveSnapshot(liveResults, liveResultsLock));

        // Build suites from stdout-parsed results
        var suites = BuildSuitesFromLiveResults(projectPath, liveResults, liveResultsLock);

        var totalTests = suites.SelectMany(s => s.Tests).Count();
        log?.Write("MtpRunner",
            $"Result: {totalTests} test(s) in {suites.Count} suite(s)");

        // Surface stderr when there are no results (likely a runner-level failure)
        string? runnerError = null;
        if (suites.Count == 0 && !stderrLines.IsEmpty)
        {
            runnerError = string.Join(Environment.NewLine, stderrLines.OrderBy(x => x));
            log?.Write("MtpRunner", $"RunnerError: {runnerError}");
        }

        return new ProjectTestResult(projectPath, suites, runnerError, [], Crashed: false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void RecordResult(
        MtpParsedResult parsed,
        Dictionary<string, (TestStatus Status, string DisplayName, TimeSpan Duration, string? ErrorMessage, string? StackTrace, string? Source)> liveResults,
        object lockObj)
    {
        var displayName = TestProcessRunner.DisplayNameOf(parsed.FullyQualifiedName);

        lock (lockObj)
        {
            liveResults[parsed.FullyQualifiedName] = (
                parsed.Status,
                displayName,
                parsed.Duration,
                parsed.ErrorMessage,
                parsed.StackTrace,
                parsed.Source);
        }
    }

    private static void FireThrottledProgress(
        Action<IReadOnlyList<TestSuite>>? onProgress,
        Dictionary<string, (TestStatus Status, string DisplayName, TimeSpan Duration, string? ErrorMessage, string? StackTrace, string? Source)> liveResults,
        object lockObj,
        ref DateTimeOffset lastFire,
        int throttleMs)
    {
        if (onProgress is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        bool shouldFire;
        lock (lockObj)
        {
            shouldFire = (now - lastFire).TotalMilliseconds >= throttleMs;
            if (shouldFire)
            {
                lastFire = now;
            }
        }

        if (shouldFire)
        {
            onProgress(BuildLiveSnapshot(liveResults, lockObj));
        }
    }

    private static IReadOnlyList<TestSuite> BuildLiveSnapshot(
        Dictionary<string, (TestStatus Status, string DisplayName, TimeSpan Duration, string? ErrorMessage, string? StackTrace, string? Source)> liveResults,
        object lockObj)
    {
        List<KeyValuePair<string, (TestStatus Status, string DisplayName, TimeSpan Duration, string? ErrorMessage, string? StackTrace, string? Source)>> snapshot;
        lock (lockObj)
        {
            snapshot = [.. liveResults];
        }

        if (snapshot.Count == 0)
        {
            return [];
        }

        var results = snapshot
            .Select(kvp => new TestResult(
                FullyQualifiedName: kvp.Key,
                DisplayName: kvp.Value.DisplayName,
                Status: kvp.Value.Status,
                Duration: kvp.Value.Duration,
                Output: null,
                ErrorMessage: kvp.Value.ErrorMessage,
                StackTrace: kvp.Value.StackTrace,
                Source: kvp.Value.Source))
            .ToList();

        return [new TestSuite("…", results, DateTimeOffset.UtcNow, TimeSpan.Zero)];
    }

    /// <summary>
    /// Builds the project's final suite, named after the project (as VSTest suites are) so results
    /// can be attributed back to it.
    /// </summary>
    private static IReadOnlyList<TestSuite> BuildSuitesFromLiveResults(
        string projectPath,
        Dictionary<string, (TestStatus Status, string DisplayName, TimeSpan Duration, string? ErrorMessage, string? StackTrace, string? Source)> liveResults,
        object lockObj)
    {
        var tests = BuildLiveSnapshot(liveResults, lockObj).SelectMany(s => s.Tests).ToList();
        if (tests.Count == 0)
        {
            return [];
        }

        var totalDuration = TimeSpan.FromTicks(tests.Sum(t => t.Duration.Ticks));
        return [new TestSuite(TestProjectNames.SuiteName(projectPath), tests, DateTimeOffset.UtcNow, totalDuration)];
    }
}
