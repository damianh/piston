// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Piston.ProcessHost;

internal static class WindowsProcess
{
    internal static int Run(ProcessStartInfo start)
    {
        // CREATE_NO_WINDOW does not inherit standard handles unless STARTF_USESTDHANDLES is set.
        var startup = new StartupInfo
        {
            Size = (uint)Marshal.SizeOf<StartupInfo>(),
            Flags = 0x100,
            StandardInput = GetStdHandle(-10),
            StandardOutput = GetStdHandle(-11),
            StandardError = GetStdHandle(-12),
        };
        var arguments = start.ArgumentList.Count > 0
            ? string.Join(" ", start.ArgumentList.Select(QuoteArgument))
            : start.Arguments;
        var commandLine = new StringBuilder($"{QuoteArgument(start.FileName)} {arguments}");
        if (!CreateProcessW(start.FileName, commandLine, IntPtr.Zero, IntPtr.Zero,
            true, 0x08000000, IntPtr.Zero, null, ref startup, out var created))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        using var process = new SafeProcessHandle(created.Process, ownsHandle: true);
        using var thread = new SafeWaitHandle(created.Thread, ownsHandle: true);
        if (WaitForSingleObject(process, uint.MaxValue) != 0
            || !GetExitCodeProcess(process, out var exitCode))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return unchecked((int)exitCode);
    }

    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            _ = result.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCharacters, YCharacters, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref StartupInfo startupInfo, out ProcessInfo processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
