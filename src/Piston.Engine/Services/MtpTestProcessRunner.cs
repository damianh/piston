// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Piston.Engine.Impact;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Internal helper that runs MTP v2 test projects. The primary path drives the test app in server mode
/// (see <c>MtpTestProcessRunner.Server.cs</c>); the fallback runs <c>dotnet test</c> in
/// Microsoft.Testing.Platform mode (<c>--project &lt;csproj&gt; --no-build --output Detailed</c>) and
/// parses its stdout via <see cref="MtpStdoutParser"/>.
/// </summary>
/// <remarks>
/// MTP mode of <c>dotnet test</c> requires a <c>global.json</c> with
/// <c>"test": { "runner": "Microsoft.Testing.Platform" }</c> in the solution root, so the fallback uses
/// the solution directory as its working directory.
/// </remarks>
internal static partial class MtpTestProcessRunner
{
    /// <summary>
    /// Lists the tests in an MTP project, streaming them to <paramref name="onPartial"/>. Uses the test
    /// app's server mode when <paramref name="appPath"/> exists, otherwise (or if the server fails)
    /// <c>dotnet test --project &lt;csproj&gt; --no-build --list-tests</c>.
    /// Returns an empty list on failure, timeout, or cancellation — discovery is best-effort.
    /// </summary>
    internal static async Task<IReadOnlyList<TestResult>> DiscoverAsync(
        string projectPath,
        string? appPath,
        string solutionDirectory,
        string? filter,
        Action<IReadOnlyList<TestResult>>? onPartial,
        CancellationToken ct)
    {
        TestFilterExpression? expression = null;
        var filterSupported = string.IsNullOrWhiteSpace(filter) || TestFilterExpression.TryParse(filter, out expression);
        if (filterSupported && appPath is not null && File.Exists(appPath))
        {
            var tests = await DiscoverWithServerAsync(appPath, expression, onPartial, ct).ConfigureAwait(false);
            if (tests is not null)
            {
                return tests;
            }
        }

        return await TestListProcess.RunAsync(
            $"test --project \"{projectPath}\" --no-build --list-tests" + TestProcessRunner.FilterArgument(filter),
            solutionDirectory,
            projectPath,
            "MtpRunner",
            new TestListParser(ListAssemblyHeaderRegex().IsMatch),
            onPartial,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses MTP <c>--list-tests</c> output: each "Discovered N tests in assembly - &lt;path&gt;" header is
    /// followed by one indented test name per line, ending at the first blank or unindented line.
    /// </summary>
    internal static IReadOnlyList<TestResult> ParseListTestsOutput(IEnumerable<string> lines) =>
        TestListParser.ParseAll(new TestListParser(ListAssemblyHeaderRegex().IsMatch), lines);

    [GeneratedRegex(@"^Discovered \d+ tests? in assembly\b", RegexOptions.IgnoreCase)]
    private static partial Regex ListAssemblyHeaderRegex();

    /// <summary>
    /// Runs an MTP project. Uses the test app's server mode when <paramref name="appPath"/> exists, which
    /// reports each test as it starts; falls back to <c>dotnet test</c> when server mode is unavailable or
    /// the filter is unsupported. Coverage is not collected for MTP projects.
    /// </summary>
    internal static async Task<ProjectTestResult> RunAsync(
        string projectPath,
        string? appPath,
        string solutionDirectory,
        string? filter,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct)
    {
        if (appPath is not null && File.Exists(appPath))
        {
            var result = await RunWithServerAsync(projectPath, appPath, filter, onProgress, ct).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        return await RunWithDotnetTestAsync(projectPath, solutionDirectory, filter, onProgress, ct).ConfigureAwait(false);
    }

    internal static async Task<ProjectTestResult> RunWithDotnetTestAsync(
        string projectPath,
        string solutionDirectory,
        string? filter,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct)
    {
        // Build CLI args: dotnet test --project <csproj> --no-build --output Detailed
        var args = $"test --project \"{projectPath}\" --no-build --output Detailed" + TestProcessRunner.FilterArgument(filter);

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

        using var progress = onProgress is null
            ? null
            : new TrailingThrottle(() => onProgress(BuildLiveSnapshot(liveResults, liveResultsLock)), TestProcessRunner.ProgressInterval);

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
            progress?.Signal();
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

        progress?.Flush();

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
