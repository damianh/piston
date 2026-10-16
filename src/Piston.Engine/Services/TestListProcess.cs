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
    /// Runs <c>dotnet <paramref name="arguments"/></c> and parses the listed tests.
    /// Returns an empty list on failure, timeout, or cancellation — discovery is best-effort.
    /// </summary>
    internal static async Task<IReadOnlyList<TestResult>> RunAsync(
        string arguments,
        string? workingDirectory,
        string projectPath,
        string logCategory,
        Func<IEnumerable<string>, IReadOnlyList<TestResult>> parse,
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

        MsBuildLocatorGuard.RestoreOriginalEnvironment(psi);
        // The parsers match the English CLI headers.
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lines.Enqueue(e.Data);
            }
        };
        process.ErrorDataReceived += (_, _) => { };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DiscoveryTimeout);

        try
        {
            _ = process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
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
            catch (InvalidOperationException) { }
            log?.Write(logCategory, $"Discovery cancelled or timed out: {projectPath}");
            return [];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log?.Write(logCategory, $"Discovery failed to start for {projectPath}: {ex.Message}");
            return [];
        }

        var tests = parse(lines);
        log?.Write(logCategory, $"Discovered {tests.Count} test(s) in {projectPath} (exit {process.ExitCode})");
        return tests;
    }
}
