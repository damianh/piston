using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Piston.TestProcessHost;

public static class Program
{
    private static SafeFileHandle? _job;

    public static async Task<int> Main(string[] args)
    {
        // Keep the handle rooted until OS process exit: explicitly closing a job
        // containing this host would kill the host before it returns its exit code.
        _job = OperatingSystem.IsWindows() ? CreateJob() : null;
        if (_job is null && setsid() == -1)
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        var start = new ProcessStartInfo(args[0])
        {
            UseShellExecute = false,
        };
        if (args[1].Length > 0)
            start.Arguments = args[1];
        else
            foreach (var argument in args.Skip(2))
                start.ArgumentList.Add(argument);

        using var child = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start test child process.");
        await child.WaitForExitAsync();
        return child.ExitCode;
    }

    private static SafeFileHandle CreateJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>())
                || !AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job, int informationClass, ref JobLimits information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
