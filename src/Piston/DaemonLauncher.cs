// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Piston.Protocol.Transports;

namespace Piston;

/// <summary>
/// Ensures a Piston daemon is running for the given solution.
/// If no daemon is found on the expected pipe, spawns one as a detached background process
/// and waits for it to become available.
/// </summary>
internal static class DaemonLauncher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Checks whether a daemon is already listening on <paramref name="pipeName"/>.
    /// If not, spawns <c>piston daemon [solutionPath] --pipe-name [pipeName]</c> as a background process
    /// with the selecting configuration directory,
    /// then waits until the pipe is available or the timeout expires.
    /// </summary>
    internal static async Task EnsureRunningAsync(
        string solutionPath,
        string pipeName,
        int webPort,
        string configurationDirectory,
        CancellationToken ct)
    {
        if (await IsPipeAvailableAsync(pipeName, ct))
        {
            return;
        }

        using var daemon = SpawnDaemon(solutionPath, pipeName, webPort, configurationDirectory);

        await WaitForPipeAsync(pipeName, daemon, ct);
    }

    private static async Task<bool> IsPipeAvailableAsync(string pipeName, CancellationToken ct)
    {
        try
        {
            await using var transport = new NamedPipeClientTransport(pipeName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await transport.ConnectAsync(cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Process SpawnDaemon(string solutionPath, string pipeName, int webPort, string configurationDirectory)
    {
        var pistonExe = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine piston executable path.");

        var psi = CreateStartInfo(pistonExe, solutionPath, pipeName, webPort, configurationDirectory);
        var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to spawn piston daemon process.");
        }

        // The daemon is not owned by this process; the handle is only used to detect an early exit.
        Console.Error.WriteLine($"[piston] Started daemon (pipe: {pipeName})");
        return process;
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath, string solutionPath, string pipeName, int webPort,
        string? configurationDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        // Framework-dependent tool shims invoke dotnet rather than a Piston apphost.
        if (string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(typeof(DaemonLauncher).Assembly.Location);
        }

        psi.ArgumentList.Add("daemon");
        psi.ArgumentList.Add(solutionPath);
        psi.ArgumentList.Add("--no-stdio");
        psi.ArgumentList.Add("--pipe-name");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--web-port");
        psi.ArgumentList.Add(webPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (configurationDirectory is not null)
        {
            psi.ArgumentList.Add("--config-directory");
            psi.ArgumentList.Add(configurationDirectory);
        }
        return psi;
    }

    private static async Task WaitForPipeAsync(string pipeName, Process daemon, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SpawnTimeout);

        Console.Error.WriteLine("[piston] Waiting for daemon...");

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, timeoutCts.Token).ConfigureAwait(false);

            if (await IsPipeAvailableAsync(pipeName, timeoutCts.Token))
            {
                Console.Error.WriteLine("[piston] Daemon ready.");
                return;
            }

            // E.g. the worktree is owned by another daemon; its error was written to the shared stderr.
            if (daemon.HasExited)
            {
                throw new InvalidOperationException(
                    $"Piston daemon exited during startup with code {daemon.ExitCode}; see the error above.");
            }
        }

        throw new TimeoutException(
            $"Piston daemon did not start within {SpawnTimeout.TotalSeconds}s. " +
            $"Try running 'piston daemon' manually.");
    }
}
