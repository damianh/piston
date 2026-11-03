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
    /// <c>piston-*</c> temp directory that the caller deletes once it has read them. Multi-targeted coverage
    /// runs each verified framework output in its own results directory.
    /// </summary>
    internal static async Task<ProjectTestResult> RunAsync(
        string projectPath,
        string? appPath,
        string solutionDirectory,
        string? filter,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct,
        MtpCoverage coverage = MtpCoverage.Off,
        MtpWarmHostPool? warmHosts = null,
        MtpDiscoveryCache? discoveryCache = null,
        int expectedCoverageReports = 1,
        IReadOnlyList<(string Framework, string OutputPath)>? targetOutputs = null)
    {
        var log = new ExecutionLog(projectPath);
        log.Write("MtpRunner", $"Project: {projectPath}");

        string? resultsDir = null;
        string? coverageSetupError = null;
        if (coverage == MtpCoverage.Collect)
        {
            try
            {
                resultsDir = Path.Combine(Path.GetTempPath(), $"piston-{Guid.NewGuid():N}");
                _ = Directory.CreateDirectory(resultsDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                resultsDir = null;
                coverageSetupError = $"Could not create a temporary coverage directory: {ex.Message}";
            }
        }
        var coverageIssue = coverageSetupError ?? coverage switch
        {
            MtpCoverage.ExtensionMissing => "The test app does not include Microsoft.Testing.Extensions.CodeCoverage.",
            MtpCoverage.MultiTargetExtensionMissing =>
                "At least one target framework does not include Microsoft.Testing.Extensions.CodeCoverage; coverage was skipped so all targets can run.",
            MtpCoverage.OutputUnverified =>
                "Coverage was skipped because collector availability could not be verified for every target framework.",
            _ => null,
        };
        if (coverageIssue is not null)
        {
            log.Write("MtpRunner", coverageIssue);
        }
        var coverageArgs = resultsDir is null ? null : CoverageArguments(resultsDir);
        IReadOnlyList<string> coverageReportPaths = [];
        var delivered = false;
        var retainResultsDirectory = false;
        try
        {
            ProjectTestResult? result = null;
            var separateTargets = targetOutputs is { Count: > 1 };
            if (separateTargets)
            {
                var separated = await RunFrameworksAsync(
                    projectPath, solutionDirectory, filter, onProgress, ct, log, resultsDir, targetOutputs!,
                    warmHosts, discoveryCache)
                    .ConfigureAwait(false);
                result = separated.Result;
                coverageIssue ??= separated.CoverageIssue;
            }
            else if (appPath is not null && File.Exists(appPath))
            {
                // With --coverage the extension relaunches the test host under a controller, which connects
                // as a passive node; the run uses a host started for it alone, not a warm one.
                result = await RunWithServerAsync(projectPath, appPath, solutionDirectory, filter, onProgress, ct, log, warmHosts,
                    coverageArgs, discoveryCache).ConfigureAwait(false);
            }

            if (result is null)
            {
                if (resultsDir is not null)
                {
                    // A server attempt that did not start may have left a partial report behind.
                    if (!ClearDirectory(resultsDir))
                    {
                        DeleteResultsDirectory(resultsDir);
                        try
                        {
                            resultsDir = Path.Combine(Path.GetTempPath(), $"piston-{Guid.NewGuid():N}");
                            _ = Directory.CreateDirectory(resultsDir);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            resultsDir = null;
                            coverageIssue = $"Could not create a clean temporary coverage directory: {ex.Message}";
                            log.Write("MtpRunner", coverageIssue);
                        }
                    }
                }

                result = await RunWithDotnetTestAsync(projectPath, solutionDirectory, filter, onProgress, ct, log, resultsDir)
                    .ConfigureAwait(false);
            }

            if (resultsDir is not null)
            {
                try
                {
                    coverageReportPaths = separateTargets ? result.CoverageReportPaths
                        : Directory.GetFiles(resultsDir, "*.cobertura.xml", SearchOption.AllDirectories);
                    log.Write("MtpRunner", $"Coverage reports: {coverageReportPaths.Count}");
                    if (coverageReportPaths.Count == 0)
                    {
                        coverageIssue ??= "The test run produced no Cobertura coverage report.";
                        log.Write("MtpRunner", coverageIssue);
                    }
                    else if (coverageReportPaths.Count < expectedCoverageReports)
                    {
                        coverageIssue ??= $"The test run produced {coverageReportPaths.Count} coverage report(s) for {expectedCoverageReports} target frameworks.";
                        log.Write("MtpRunner", coverageIssue);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    coverageIssue = $"Could not read the temporary coverage directory: {ex.Message}";
                    log.Write("MtpRunner", coverageIssue);
                }
            }

            log.Complete();
            retainResultsDirectory = resultsDir is not null
                && result.Suites.SelectMany(suite => suite.Tests).Any(test => test.Attachments.Count > 0);
            var completed = result with
            {
                Suites = log.AttachTo(result.Suites),
                CoverageReportPaths = coverageReportPaths,
                CoverageIssue = coverageIssue,
                TemporaryDirectories = retainResultsDirectory
                    ? [.. result.TemporaryDirectories, resultsDir!]
                    : result.TemporaryDirectories,
            };
            delivered = true;
            return completed;
        }
        finally
        {
            // With reports, the caller deletes the directory after reading them.
            if (resultsDir is not null && (!delivered || (coverageReportPaths.Count == 0 && !retainResultsDirectory)))
            {
                DeleteResultsDirectory(resultsDir);
            }
        }
    }

    private static bool ClearDirectory(string directory)
    {
        var cleared = true;
        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(directory).GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Instance?.Write("MtpRunner", $"Could not inspect temporary results directory {directory}: {ex.Message}");
            return false;
        }

        foreach (var entry in entries)
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
                cleared = false;
                DiagnosticLog.Instance?.Write("MtpRunner", $"Could not clear temporary result {entry.FullName}: {ex.Message}");
            }
        }

        return cleared;
    }

    private static void DeleteResultsDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Instance?.Write("MtpRunner", $"Could not remove temporary results directory {directory}: {ex.Message}");
        }
    }

    private static async Task<(ProjectTestResult Result, string? CoverageIssue)> RunFrameworksAsync(
        string projectPath,
        string solutionDirectory,
        string? filter,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct,
        ExecutionLog log,
        string? resultsDirectory,
        IReadOnlyList<(string Framework, string OutputPath)> targets,
        MtpWarmHostPool? warmHosts,
        MtpDiscoveryCache? discoveryCache)
    {
        var tests = new List<TestResult>();
        var reports = new List<string>();
        var errors = new List<string>();
        var coverageIssues = new List<string>();
        var crashed = false;
        int? failedExitCode = null;
        for (var index = 0; index < targets.Count && !ct.IsCancellationRequested; index++)
        {
            var (framework, appPath) = targets[index];
            string? targetResultsDirectory = null;
            if (resultsDirectory is not null)
            {
                try
                {
                    targetResultsDirectory = Path.Combine(resultsDirectory, $"framework-{index}");
                    _ = Directory.CreateDirectory(targetResultsDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    targetResultsDirectory = null;
                    coverageIssues.Add($"Could not create the coverage directory for {framework}: {ex.Message}");
                }
            }

            var previous = tests.ToArray();
            Action<IReadOnlyList<TestSuite>>? reportProgress = onProgress is null ? null : suites =>
            {
                var current = previous.Concat(QualifyTests(suites, framework)).ToList();
                onProgress([new TestSuite("…", current, DateTimeOffset.UtcNow,
                    TimeSpan.FromTicks(current.Sum(test => test.Duration.Ticks)))]);
            };
            log.Write("MtpRunner", $"Running target framework {framework}");
            log.ExitCode = null;
            ProjectTestResult? result = null;
            if (File.Exists(appPath))
            {
                result = await RunWithServerAsync(projectPath, appPath, solutionDirectory, filter, reportProgress,
                    ct, log, warmHosts, targetResultsDirectory is null ? null : CoverageArguments(targetResultsDirectory),
                    discoveryCache).ConfigureAwait(false);
                if (result is null && targetResultsDirectory is not null && !ClearDirectory(targetResultsDirectory))
                {
                    coverageIssues.Add($"Could not clear incomplete coverage results for {framework}; coverage was skipped.");
                    targetResultsDirectory = null;
                }
            }

            result ??= await RunWithDotnetTestAsync(
                    projectPath, solutionDirectory, filter, reportProgress, ct, log, targetResultsDirectory, framework)
                .ConfigureAwait(false);
            tests.AddRange(QualifyTests(result.Suites, framework));
            crashed |= result.Crashed;
            if (result.RunnerError is { Length: > 0 } error)
            {
                errors.Add($"{framework}: {error}");
            }

            if (log.ExitCode is { } code && code != 0)
            {
                failedExitCode ??= code;
            }

            if (targetResultsDirectory is not null)
            {
                try
                {
                    var targetReports = Directory.GetFiles(targetResultsDirectory, "*.cobertura.xml", SearchOption.AllDirectories);
                    if (targetReports.Length == 0)
                    {
                        coverageIssues.Add($"The test run produced no Cobertura coverage report for {framework}.");
                    }

                    reports.AddRange(targetReports);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    coverageIssues.Add($"Could not read the coverage directory for {framework}: {ex.Message}");
                }
            }
        }

        if (failedExitCode is not null)
        {
            log.ExitCode = failedExitCode;
        }

        IReadOnlyList<TestSuite> suites = tests.Count == 0 ? []
            : [new TestSuite(TestProjectNames.SuiteName(projectPath), tests, DateTimeOffset.UtcNow,
                TimeSpan.FromTicks(tests.Sum(test => test.Duration.Ticks)))];
        var issue = coverageIssues.Count == 0 ? null : string.Join(" ", coverageIssues);
        if (issue is not null)
        {
            log.Write("MtpRunner", issue);
        }

        return (new ProjectTestResult(projectPath, suites, errors.Count == 0 ? null : string.Join("\n", errors), reports, crashed)
        {
            Log = log,
        }, issue);
    }

    private static IEnumerable<TestResult> QualifyTests(IReadOnlyList<TestSuite> suites, string framework) =>
        suites.SelectMany(suite => suite.Tests).Select(test => test with { TargetFramework = framework });

    /// <summary>
    /// Microsoft.Testing.Extensions.CodeCoverage options writing Cobertura to <paramref name="resultsDir"/>.
    /// Multi-targeted runs give each framework its own directory to prevent report name collisions.
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
        string? resultsDirectory = null,
        string? targetFramework = null)
    {
        // Build CLI args: dotnet test --project <csproj> --no-build --output Detailed --no-ansi.
        // Without --no-ansi, CI environments (e.g. GitHub Actions) get coloured output even when redirected,
        // which the stdout parser cannot match.
        var args = new List<string> { "test", "--project", projectPath, "--no-build", "--output", "Detailed", "--no-ansi" };
        if (!string.IsNullOrWhiteSpace(filter))
        {
            args.AddRange(["--filter", filter]);
        }

        if (targetFramework is not null)
        {
            args.AddRange(["--framework", targetFramework]);
        }

        if (resultsDirectory is not null)
        {
            args.AddRange(["--coverage", "--coverage-output-format", "cobertura", "--results-directory", resultsDirectory]);
        }

        var log = runLog ?? new ExecutionLog(projectPath);
        log.Runner = "MTP dotnet test";
        log.Write("MtpRunner", $"WorkDir: {solutionDirectory}");
        log.Write("MtpRunner", $"Args: dotnet {string.Join(" ", args.Select((argument, index) =>
            index > 0 && args[index - 1] is "--project" or "--filter" or "--framework" or "--results-directory"
                ? $"\"{argument}\"" : argument))}");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = solutionDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in args)
        {
            psi.ArgumentList.Add(argument);
        }
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
            suites = [suites[0] with { Tests = AttachArtifacts(suites[0].Tests, parser.Attachments, log) }];
        }

        var totalTests = suites.SelectMany(s => s.Tests).Count();
        log.Write("MtpRunner",
            $"Result: {totalTests} test(s) in {suites.Count} suite(s)");

        // A failed runner can report its diagnostics on stdout instead of stderr.
        string? runnerError = null;
        if (suites.Count == 0)
        {
            if (!stderrLines.IsEmpty)
            {
                runnerError = string.Join(Environment.NewLine, stderrLines.OrderBy(x => x));
            }
            else if (process.ExitCode != 0 && !(process.ExitCode == 8 && !string.IsNullOrWhiteSpace(filter)))
            {
                runnerError = $"dotnet test exited with code {process.ExitCode} without reporting tests.";
            }

            if (runnerError is not null)
            {
                log.Write("MtpRunner", $"RunnerError: {runnerError}");
            }
        }

        return new ProjectTestResult(projectPath, suites, runnerError, [], Crashed: false) { Log = log };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static IReadOnlyList<TestResult> AttachArtifacts(
        IReadOnlyList<TestResult> results,
        IReadOnlyList<MtpParsedAttachment> attachments,
        ExecutionLog log)
    {
        var tests = results.ToList();
        foreach (var attachment in attachments)
        {
            var matching = tests.Where(test =>
                string.Equals(test.DisplayName, attachment.TestName, StringComparison.Ordinal)
                || string.Equals(test.FullyQualifiedName, attachment.TestName, StringComparison.Ordinal))
                .ToList();
            if (matching.Count != 1)
            {
                log.Write("MtpRunner",
                    $"Cannot associate artifact '{attachment.Path}' with test '{attachment.TestName}': " +
                    $"{matching.Count} matching results; the artifact summary does not identify its producing framework.");
                continue;
            }

            var test = matching[0];
            tests[tests.IndexOf(test)] = test with
            {
                Attachments = [.. test.Attachments, new TestAttachment(attachment.Path, Path.GetFileName(attachment.Path))],
            };
        }

        return tests;
    }

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
