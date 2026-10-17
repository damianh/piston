// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Piston.ProcessHost;

/// <summary>Establishes descendant ownership before starting a test process.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var job = OperatingSystem.IsWindows() ? OpenJobObjectW(0x0001, false, args[0]) : null;
        if (OperatingSystem.IsWindows())
        {
            if (job!.IsInvalid || !AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle))
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

        using var child = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the test process.");
        await child.WaitForExitAsync().ConfigureAwait(false);
        return child.ExitCode;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
