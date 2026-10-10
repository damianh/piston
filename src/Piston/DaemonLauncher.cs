using System.Diagnostics;
using Piston.Protocol.Transports;

namespace Piston.Cli;

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
    /// If not, spawns <c>piston daemon [solutionPath] --pipe-name [pipeName]</c> as a background process,
    /// then waits until the pipe is available or the timeout expires.
    /// </summary>
    internal static async Task EnsureRunningAsync(
        string solutionPath,
        string pipeName,
        int webPort,
        CancellationToken ct)
    {
        if (await IsPipeAvailableAsync(pipeName, ct))
            return;

        SpawnDaemon(solutionPath, pipeName, webPort);

        await WaitForPipeAsync(pipeName, ct);
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

    private static void SpawnDaemon(string solutionPath, string pipeName, int webPort)
    {
        var pistonExe = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine piston executable path.");

        var psi = CreateStartInfo(pistonExe, solutionPath, pipeName, webPort);
        var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Failed to spawn piston daemon process.");

        // Detach — we don't own the daemon lifecycle
        process.Dispose();

        Console.Error.WriteLine($"[piston] Started daemon (pipe: {pipeName})");
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath, string solutionPath, string pipeName, int webPort)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = executablePath,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = false,
            RedirectStandardOutput = false,
            RedirectStandardError  = false,
        };

        // Framework-dependent tool shims invoke dotnet rather than a Piston apphost.
        if (string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(DaemonLauncher).Assembly.Location);

        psi.ArgumentList.Add("daemon");
        psi.ArgumentList.Add(solutionPath);
        psi.ArgumentList.Add("--pipe-name");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--web-port");
        psi.ArgumentList.Add(webPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return psi;
    }

    private static async Task WaitForPipeAsync(string pipeName, CancellationToken ct)
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
        }

        throw new TimeoutException(
            $"Piston daemon did not start within {SpawnTimeout.TotalSeconds}s. " +
            $"Try running 'piston daemon' manually.");
    }
}
