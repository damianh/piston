// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Internal helper that runs MTP v2 test projects. The primary path drives the test app in server mode
/// (see <c>MtpTestProcessRunner.Server.cs</c>); the fallback runs <c>dotnet test</c> in
/// Microsoft.Testing.Platform mode (<c>--project &lt;csproj&gt; --no-build --output Detailed --no-ansi</c>) and
/// parses its stdout via <see cref="MtpStdoutParser"/>.
/// </summary>
/// <remarks>
/// MTP mode of <c>dotnet test</c> requires a <c>global.json</c> with
/// <c>"test": { "runner": "Microsoft.Testing.Platform" }</c> in the solution root, so the fallback uses
/// the solution directory as its working directory. <c>dotnet test</c> starts the test app there too, so
/// server mode does the same.
/// </remarks>
internal static partial class MtpTestProcessRunner
{
    /// <summary>
    /// Lists the tests in an MTP project, streaming them to <paramref name="onPartial"/>. Uses the test
    /// app's server mode when <paramref name="appPath"/> exists, otherwise (or if the server fails)
    /// <c>dotnet test --project &lt;csproj&gt; --no-build --list-tests --no-ansi</c>.
    /// Returns an empty list on failure, timeout, or cancellation — discovery is best-effort.
    /// </summary>
    internal static async Task<IReadOnlyList<TestResult>> DiscoverAsync(
        string projectPath,
        string? appPath,
        string solutionDirectory,
        string? filter,
        Action<IReadOnlyList<TestResult>>? onPartial,
        CancellationToken ct,
        MtpWarmHostPool? warmHosts = null)
    {
        TestFilterExpression? expression = null;
        var filterSupported = string.IsNullOrWhiteSpace(filter) || TestFilterExpression.TryParse(filter, out expression);
        if (filterSupported && appPath is not null && File.Exists(appPath))
        {
            var tests = await DiscoverWithServerAsync(appPath, solutionDirectory, expression, onPartial, ct, warmHosts).ConfigureAwait(false);
            if (tests is not null)
            {
                return tests;
            }
        }

        return await TestListProcess.RunAsync(
            $"test --project \"{projectPath}\" --no-build --list-tests --no-ansi" + TestProcessRunner.FilterArgument(filter),
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
    /// the filter is unsupported. With <see cref="MtpCoverage.Collect"/>, Cobertura reports are written to a
    /// <c>piston-*</c> temp directory that the caller deletes once it has read them.
    /// </summary>
    internal static async Task<ProjectTestResult> RunAsync(
        string projectPath,
        string? appPath,
        string solutionDirectory,
        string? filter,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct,
        MtpCoverage coverage = MtpCoverage.Off,
        MtpWarmHostPool? warmHosts = null)
    {
        var log = new ExecutionLog(projectPath);
        log.Write("MtpRunner", $"Project: {projectPath}");

        string? resultsDir = null;
        if (coverage == MtpCoverage.Collect)
        {
            resultsDir = Path.Combine(Path.GetTempPath(), $"piston-{Guid.NewGuid():N}");
            _ = Directory.CreateDirectory(resultsDir);
        }
        else if (coverage == MtpCoverage.ExtensionMissing)
        {
            log.Write("MtpRunner",
                "Coverage not collected: the project does not reference Microsoft.Testing.Extensions.CodeCoverage");
        }

        var coverageArgs = resultsDir is null ? null : CoverageArguments(resultsDir);
        IReadOnlyList<string> coverageReportPaths = [];
        try
        {
            ProjectTestResult? result = null;
            if (appPath is not null && File.Exists(appPath))
            {
                // With --coverage the extension relaunches the test host under a controller, which connects
                // as a passive node; the run uses a host started for it alone, not a warm one.
                result = await RunWithServerAsync(projectPath, appPath, solutionDirectory, filter, onProgress, ct, log, warmHosts,
                    coverageArgs).ConfigureAwait(false);
            }

            if (result is null)
            {
                if (resultsDir is not null)
                {
                    // A server attempt that did not start may have left a partial report behind.
                    ClearDirectory(resultsDir);
                }

                result = await RunWithDotnetTestAsync(projectPath, solutionDirectory, filter, onProgress, ct, log, coverageArgs)
                    .ConfigureAwait(false);
            }

            if (resultsDir is not null)
            {
                coverageReportPaths = Directory.GetFiles(resultsDir, "*.cobertura.xml", SearchOption.AllDirectories);
                log.Write("MtpRunner", $"Coverage reports: {coverageReportPaths.Count}");
            }

            log.Complete();
            return result with { Suites = log.AttachTo(result.Suites), CoverageReportPaths = coverageReportPaths };
        }
        finally
        {
            // With reports, the caller deletes the directory after reading them.
            if (resultsDir is not null && coverageReportPaths.Count == 0)
            {
                try { Directory.Delete(resultsDir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    private static void ClearDirectory(string directory)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo subdirectory)
                {
                    subdirectory.Delete(recursive: true);
                }
                else
                {
                    entry.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover report is at worst merged with the fallback's.
            }
        }
    }

    /// <summary>
    /// Microsoft.Testing.Extensions.CodeCoverage options writing Cobertura to <paramref name="resultsDir"/>.
    /// The report name is left to the extension, which makes it unique per test app: a multi-targeted project
    /// writes one per framework.
    /// </summary>
    internal static string CoverageArguments(string resultsDir) =>
        $"--coverage --coverage-output-format cobertura --results-directory \"{resultsDir}\"";

    internal static async Task<ProjectTestResult> RunWithDotnetTestAsync(
        string projectPath,
        string solutionDirectory,
        string? filter,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct,
        ExecutionLog? runLog = null,
        string? extraArguments = null)
    {
        // Build CLI args: dotnet test --project <csproj> --no-build --output Detailed --no-ansi.
        // Without --no-ansi, CI environments (e.g. GitHub Actions) get coloured output even when redirected,
        // which the stdout parser cannot match.
        var args = $"test --project \"{projectPath}\" --no-build --output Detailed --no-ansi" + TestProcessRunner.FilterArgument(filter);
        if (!string.IsNullOrWhiteSpace(extraArguments))
        {
            args += " " + extraArguments.Trim();
        }

        var log = runLog ?? new ExecutionLog(projectPath);
        log.Runner = "MTP dotnet test";
        log.Write("MtpRunner", $"WorkDir: {solutionDirectory}");
        log.Write("MtpRunner", $"Args: dotnet {args}");

        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = solutionDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Reused MSBuild nodes can retain the redirected pipes after dotnet exits.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        if (ChildProcessEnvironment.Describe(ChildProcessEnvironment.Prepare(psi)) is { } environmentNote)
        {
            log.Write("MtpRunner", environmentNote);
        }

        var parser = new MtpStdoutParser();
        var liveResults = new Dictionary<string, MtpParsedResult>(StringComparer.Ordinal);
        var liveResultsLock = new object();
        var stderrLines = new System.Collections.Concurrent.ConcurrentBag<string>();

        using var progress = onProgress is null
            ? null
            : new TrailingThrottle(() => onProgress(BuildLiveSnapshot(liveResults, liveResultsLock)), TestProcessRunner.ProgressInterval);

        using var ownedProcess = new OwnedTestProcess(psi);
        var process = ownedProcess.Process;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            log.Write("MtpRunner:stdout", e.Data);

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
                log.Write("MtpRunner:stderr", e.Data.Trim());
                stderrLines.Add(e.Data.Trim());
            }
        };

        ownedProcess.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            _ = await RedirectedProcessDrain.WaitForExitAndDrainAsync(
                ownedProcess,
                RedirectedProcessDrain.DefaultTimeout,
                ct,
                message => log.Write("MtpRunner", message)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RedirectedProcessDrain.KillAndWaitAsync(
                ownedProcess,
                CancellationToken.None,
                message => log.Write("MtpRunner", message)).ConfigureAwait(false);
            log.Write("MtpRunner", "Cancelled — returning empty result");
            return new ProjectTestResult(projectPath, [], null, [], Crashed: false);
        }

        log.ExitCode = process.ExitCode;
        log.Write("MtpRunner", $"ExitCode: {process.ExitCode}");

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
        if (parser.Attachments.Count > 0 && suites.Count > 0)
        {
            var tests = suites.SelectMany(s => s.Tests).ToList();
            foreach (var attachment in parser.Attachments)
            {
                var matching = tests.Where(test =>
                    string.Equals(test.DisplayName, attachment.TestName, StringComparison.Ordinal)
                    || string.Equals(test.FullyQualifiedName, attachment.TestName, StringComparison.Ordinal))
                    .ToList();
                if (matching.Count == 1)
                {
                    var test = matching[0];
                    var updated = test with
                    {
                        Attachments = [.. test.Attachments, new TestAttachment(attachment.Path, Path.GetFileName(attachment.Path))],
                    };
                    tests[tests.IndexOf(test)] = updated;
                }
            }

            suites = [suites[0] with { Tests = tests }];
        }

        var totalTests = suites.SelectMany(s => s.Tests).Count();
        log.Write("MtpRunner",
            $"Result: {totalTests} test(s) in {suites.Count} suite(s)");

        // Surface stderr when there are no results (likely a runner-level failure)
        string? runnerError = null;
        if (suites.Count == 0 && !stderrLines.IsEmpty)
        {
            runnerError = string.Join(Environment.NewLine, stderrLines.OrderBy(x => x));
            log.Write("MtpRunner", $"RunnerError: {runnerError}");
        }

        return new ProjectTestResult(projectPath, suites, runnerError, [], Crashed: false) { Log = log };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void RecordResult(
        MtpParsedResult parsed,
        Dictionary<string, MtpParsedResult> liveResults,
        object lockObj)
    {
        lock (lockObj)
        {
            liveResults[parsed.Key] = parsed;
        }
    }

    private static IReadOnlyList<TestSuite> BuildLiveSnapshot(
        Dictionary<string, MtpParsedResult> liveResults,
        object lockObj)
    {
        List<KeyValuePair<string, MtpParsedResult>> snapshot;
        lock (lockObj)
        {
            snapshot = [.. liveResults];
        }

        if (snapshot.Count == 0)
        {
            return [];
        }

        // Results are only told apart by framework when the project ran under more than one.
        var multiTargeted = snapshot.Select(kvp => kvp.Value.TargetFramework).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

        var results = snapshot
            .Select(kvp => new TestResult(
                FullyQualifiedName: kvp.Value.FullyQualifiedName,
                DisplayName: TestProcessRunner.DisplayNameOf(kvp.Value.FullyQualifiedName),
                Status: kvp.Value.Status,
                Duration: kvp.Value.Duration,
                Output: kvp.Value.Output,
                ErrorMessage: kvp.Value.ErrorMessage,
                StackTrace: kvp.Value.StackTrace,
                Source: kvp.Value.Source)
            {
                TargetFramework = multiTargeted ? kvp.Value.TargetFramework : null,
                ErrorOutput = kvp.Value.ErrorOutput,
            })
            .ToList();

        return [new TestSuite("…", results, DateTimeOffset.UtcNow, TimeSpan.Zero)];
    }

    /// <summary>
    /// Builds the project's final suite, named after the project (as VSTest suites are) so results
    /// can be attributed back to it.
    /// </summary>
    private static IReadOnlyList<TestSuite> BuildSuitesFromLiveResults(
        string projectPath,
        Dictionary<string, MtpParsedResult> liveResults,
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
