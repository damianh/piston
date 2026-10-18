// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Piston.Engine.Impact;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>Runs a <c>dotnet test ... --list-tests</c> discovery process and parses its stdout.</summary>
internal static class TestListProcess
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs <c>dotnet <paramref name="arguments"/></c> and parses the listed tests as they are printed,
    /// reporting the cumulative list so far to <paramref name="onPartial"/> (throttled).
    /// Returns an empty list on failure, timeout, or cancellation — discovery is best-effort.
    /// </summary>
    internal static async Task<IReadOnlyList<TestResult>> RunAsync(
        string arguments,
        string? workingDirectory,
        string projectPath,
        string logCategory,
        TestListParser parser,
        Action<IReadOnlyList<TestResult>>? onPartial,
        CancellationToken ct)
    {
        var log = DiagnosticLog.Instance;
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        // Reused MSBuild nodes can retain the redirected pipes after dotnet exits,
        // leaving WaitForExitAsync waiting indefinitely for output EOF.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        MsBuildLocatorGuard.RestoreOriginalEnvironment(psi);
        // The parsers match the English CLI headers.
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        var parserLock = new object();
        using var partials = onPartial is null
            ? null
            : new TrailingThrottle(
                () =>
                {
                    IReadOnlyList<TestResult> snapshot;
                    lock (parserLock)
                    {
                        snapshot = [.. parser.Results];
                    }

                    onPartial(snapshot);
                },
                TestProcessRunner.ProgressInterval);

        OwnedTestProcess ownedProcess;
        try
        {
            ownedProcess = new OwnedTestProcess(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log?.Write(logCategory, $"Discovery failed to start for {projectPath}: {ex.Message}");
            return [];
        }

        using var processLifetime = ownedProcess;
        var process = ownedProcess.Process;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            bool added;
            lock (parserLock)
            {
                added = parser.ProcessLine(e.Data);
            }

            if (added)
            {
                partials?.Signal();
            }
        };
        process.ErrorDataReceived += (_, _) => { };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DiscoveryTimeout);

        try
        {
            ownedProcess.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _ = await RedirectedProcessDrain.WaitForExitAndDrainAsync(
                ownedProcess,
                RedirectedProcessDrain.DefaultTimeout,
                timeout.Token,
                message => log?.Write(logCategory, message)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RedirectedProcessDrain.KillAndWaitAsync(
                ownedProcess,
                CancellationToken.None,
                message => log?.Write(logCategory, message)).ConfigureAwait(false);

            log?.Write(logCategory, $"Discovery cancelled or timed out: {projectPath}");
            return [];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log?.Write(logCategory, $"Discovery failed to start for {projectPath}: {ex.Message}");
            return [];
        }

        partials?.Dispose();
        IReadOnlyList<TestResult> tests;
        lock (parserLock)
        {
            tests = [.. parser.Results];
        }

        log?.Write(logCategory, $"Discovered {tests.Count} test(s) in {projectPath} (exit {process.ExitCode})");
        return tests;
    }
}

/// <summary>
/// Incremental parser for <c>--list-tests</c> output: test names are the indented lines following a
/// header line, up to the first empty or unindented line (output for several assemblies repeats the header).
/// </summary>
internal sealed class TestListParser(Func<string, bool> isHeader)
{
    private readonly List<TestResult> _results = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _inList;

    /// <summary>The distinct tests parsed so far, in output order.</summary>
    public IReadOnlyList<TestResult> Results => _results;

    /// <summary>Processes one output line; returns <see langword="true"/> when it added a test.</summary>
    public bool ProcessLine(string line)
    {
        if (isHeader(line))
        {
            _inList = true;
            return false;
        }

        if (!_inList)
        {
            return false;
        }

        if (line.Length == 0 || !char.IsWhiteSpace(line[0]))
        {
            _inList = false;
            return false;
        }

        var name = line.Trim();
        if (name.Length == 0 || !_seen.Add(name))
        {
            return false;
        }

        _results.Add(new TestResult(name, TestProcessRunner.DisplayNameOf(name), TestStatus.NotRun,
            TimeSpan.Zero, null, null, null, null));
        return true;
    }

    /// <summary>Parses all <paramref name="lines"/> and returns the tests found.</summary>
    public static IReadOnlyList<TestResult> ParseAll(TestListParser parser, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            _ = parser.ProcessLine(line);
        }

        return parser.Results;
    }
}
