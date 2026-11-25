// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Piston.ProcessHost;

/// <summary>Establishes descendant ownership before starting a command.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args[0] == "--watch-owner")
        {
            return await WatchOwnerAsync(args[1], int.Parse(args[2], CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }

        if (OperatingSystem.IsWindows())
        {
            using var job = OpenJobObjectW(0x0001, false, args[0]);
            if (job.IsInvalid || !AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        else if (setsid() == -1)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var start = CreateChildStartInfo(args);
        if (OperatingSystem.IsWindows())
        {
            return WindowsProcess.Run(start);
        }

        // The guardian stays in this group after the launcher exits, protecting descendants
        // that retain output handles during the engine's drain grace period.
        using var guardian = await StartGuardianAsync(args[0]).ConfigureAwait(false);
        using var child = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the target process.");
        await child.WaitForExitAsync().ConfigureAwait(false);
        return child.ExitCode;
    }

    private static async Task<Process> StartGuardianAsync(string pipeName)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--watch-owner");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        var guardian = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the owner-lifetime guardian.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (await guardian.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) != "READY")
            {
                throw new IOException("Owner-lifetime guardian failed to start: " +
                    await guardian.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false));
            }
            return guardian;
        }
        catch
        {
            if (!guardian.HasExited)
            {
                guardian.Kill();
            }
            guardian.Dispose();
            throw;
        }
    }

    private static async Task<int> WatchOwnerAsync(string pipeName, int groupId)
    {
        using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(10_000).ConfigureAwait(false);
        Console.WriteLine("READY");
        await Console.Out.FlushAsync().ConfigureAwait(false);
        try
        {
            var buffer = new byte[1];
            _ = await pipe.ReadAsync(buffer).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A broken connection, like EOF, means the engine no longer owns this group.
        }

        if (kill(-groupId, 9) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return 0;
    }

    internal static ProcessStartInfo CreateChildStartInfo(string[] args)
    {
        var start = new ProcessStartInfo(args[1])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (args[2].Length > 0)
        {
            start.Arguments = args[2];
        }
        else
        {
            foreach (var argument in args.Skip(3))
            {
                start.ArgumentList.Add(argument);
            }
        }

        return start;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
