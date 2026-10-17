// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Piston.Engine.Services;

/// <summary>Owns a job or process group that survives the test process's exit.</summary>
internal sealed class OwnedTestProcess : IDisposable
{
    private readonly SafeFileHandle? _job;
    private bool _started;
    private bool _cleanedUp;

    internal OwnedTestProcess(ProcessStartInfo startInfo)
    {
        var jobName = OperatingSystem.IsWindows() ? $"piston-tests-{Guid.NewGuid():N}" : string.Empty;
        _job = OperatingSystem.IsWindows() ? CreateJob(jobName) : null;
        var fileName = startInfo.FileName;
        var arguments = startInfo.Arguments;
        var argumentList = startInfo.ArgumentList.ToArray();
        startInfo.FileName = "dotnet";
        startInfo.Arguments = string.Empty;
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(typeof(ProcessHost.Program).Assembly.Location);
        startInfo.ArgumentList.Add(jobName);
        startInfo.ArgumentList.Add(fileName);
        startInfo.ArgumentList.Add(arguments);
        foreach (var argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process = new Process { StartInfo = startInfo };
    }

    internal Process Process { get; }

    internal void Start()
    {
        _ = Process.Start();
        _started = true;
    }

    internal string? KillProcessTree()
    {
        if (!_started || _cleanedUp)
        {
            return null;
        }

        string? error = null;
        if (_job is not null)
        {
            if (!TerminateJobObject(_job, 1))
            {
                error = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            }
        }
        else if (kill(-Process.Id, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
        {
            error = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
        }

        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (Process.HasExited)
        {
        }
        catch (Win32Exception) when (Process.HasExited)
        {
        }
        catch (Exception ex) when (ex is Win32Exception or AggregateException)
        {
            error = ex.Message;
        }

        return error;
    }

    internal void CompleteCleanup() => _cleanedUp = true;

    internal async Task WaitForTreeExitAsync(CancellationToken ct)
    {
        while (HasLiveProcesses())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
        }
    }

    private bool HasLiveProcesses()
    {
        if (_job is not null)
        {
            if (!QueryInformationJobObject(_job, 1, out var accounting, (uint)Marshal.SizeOf<JobAccounting>(), out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return accounting.ActiveProcesses != 0;
        }

        if (kill(-Process.Id, 0) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == 3 ? false : throw new Win32Exception(error);
        }

        var live = false;
        foreach (var candidate in System.Diagnostics.Process.GetProcesses())
        {
            using (candidate)
            {
                if (getpgid(candidate.Id) == Process.Id && !candidate.HasExited)
                {
                    live = true;
                }
            }
        }

        return live;
    }

    public void Dispose()
    {
        var error = KillProcessTree();
        if (error is not null)
        {
            DiagnosticLog.Instance?.Write("TestProcess", $"Could not terminate the test process tree: {error}");
        }

        Process.Dispose();
        _job?.Dispose();
    }

    private static SafeFileHandle CreateJob(string name)
    {
        var job = CreateJobObjectW(IntPtr.Zero, name);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        return job;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job, int informationClass, ref JobLimits information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job, int informationClass, out JobAccounting information, uint length, out uint returnedLength);
}
