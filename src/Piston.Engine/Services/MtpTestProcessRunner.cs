using System.Diagnostics;
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
internal static class MtpTestProcessRunner
{
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
            args += $" --filter \"{filter}\"";

        var log = DiagnosticLog.Instance;
        log?.Write("MtpRunner", $"Project: {projectPath}");
        log?.Write("MtpRunner", $"WorkDir: {solutionDirectory}");
        log?.Write("MtpRunner", $"Args: dotnet {args}");

        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory       = solutionDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        var parser          = new MtpStdoutParser();
        var liveResults     = new Dictionary<string, (TestStatus Status, string DisplayName, TimeSpan Duration, string? ErrorMessage, string? StackTrace, string? Source)>(StringComparer.Ordinal);
        var liveResultsLock = new object();
        var stderrLines     = new System.Collections.Concurrent.ConcurrentBag<string>();

        var lastProgressFire = DateTimeOffset.MinValue;
        const int progressThrottleMs = 150;

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            log?.Write("MtpRunner:stdout", e.Data);

            MtpParsedResult? parsed;
            lock (liveResultsLock)
            {
                parsed = parser.ProcessLine(e.Data);
            }

            if (parsed is null) return;

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
            RecordResult(last, liveResults, liveResultsLock);

        // Fire final progress update
        onProgress?.Invoke(BuildLiveSnapshot(liveResults, liveResultsLock));

        // Build suites from stdout-parsed results
        var suites = BuildSuitesFromLiveResults(liveResults, liveResultsLock);

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
        var dot         = parsed.FullyQualifiedName.LastIndexOf('.');
        var displayName = dot >= 0 ? parsed.FullyQualifiedName[(dot + 1)..] : parsed.FullyQualifiedName;

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
        if (onProgress is null) return;

        var now = DateTimeOffset.UtcNow;
        bool shouldFire;
        lock (lockObj)
        {
            shouldFire = (now - lastFire).TotalMilliseconds >= throttleMs;
            if (shouldFire) lastFire = now;
        }

        if (shouldFire)
            onProgress(BuildLiveSnapshot(liveResults, lockObj));
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

        if (snapshot.Count == 0) return [];

        var results = snapshot
            .Select(kvp => new TestResult(
                FullyQualifiedName: kvp.Key,
                DisplayName:        kvp.Value.DisplayName,
                Status:             kvp.Value.Status,
                Duration:           kvp.Value.Duration,
                Output:             null,
                ErrorMessage:       kvp.Value.ErrorMessage,
                StackTrace:         kvp.Value.StackTrace,
                Source:             kvp.Value.Source))
            .ToList();

        return [new TestSuite("…", results, DateTimeOffset.UtcNow, TimeSpan.Zero)];
    }

    private static IReadOnlyList<TestSuite> BuildSuitesFromLiveResults(
        Dictionary<string, (TestStatus Status, string DisplayName, TimeSpan Duration, string? ErrorMessage, string? StackTrace, string? Source)> liveResults,
        object lockObj)
    {
        List<KeyValuePair<string, (TestStatus Status, string DisplayName, TimeSpan Duration, string? ErrorMessage, string? StackTrace, string? Source)>> snapshot;
        lock (lockObj)
        {
            snapshot = [.. liveResults];
        }

        if (snapshot.Count == 0) return [];

        // Group by namespace (prefix before last dot in FQN)
        var byNamespace = snapshot
            .GroupBy(kvp =>
            {
                var fqn = kvp.Key;
                var dot = fqn.LastIndexOf('.');
                return dot >= 0 ? fqn[..dot] : fqn;
            })
            .ToList();

        var suites = new List<TestSuite>();
        foreach (var group in byNamespace)
        {
            var tests = group
                .Select(kvp => new TestResult(
                    FullyQualifiedName: kvp.Key,
                    DisplayName:        kvp.Value.DisplayName,
                    Status:             kvp.Value.Status,
                    Duration:           kvp.Value.Duration,
                    Output:             null,
                    ErrorMessage:       kvp.Value.ErrorMessage,
                    StackTrace:         kvp.Value.StackTrace,
                    Source:             kvp.Value.Source))
                .ToList();

            var totalDuration = TimeSpan.FromTicks(tests.Sum(t => t.Duration.Ticks));
            suites.Add(new TestSuite(group.Key, tests, DateTimeOffset.UtcNow, totalDuration));
        }

        return suites;
    }
}
