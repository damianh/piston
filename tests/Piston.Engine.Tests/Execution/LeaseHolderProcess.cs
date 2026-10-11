// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Piston.Engine.Tests.Execution;

/// <summary>Runs <c>Piston.ExecutionLeaseHolder</c> to hold ownership or admission in another process.</summary>
internal sealed class LeaseHolderProcess : IDisposable
{
    private readonly Process _process;
    private readonly BlockingCollection<string> _lines = new();

    private LeaseHolderProcess(Process process)
    {
        _process = process;
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) _lines.CompleteAdding();
            else _lines.Add(e.Data);
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public int ProcessId => _process.Id;

    public static LeaseHolderProcess Start(IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(typeof(ExecutionLeaseHolder.Program).Assembly.Location);
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            startInfo.Environment[key] = value;
        return new LeaseHolderProcess(Process.Start(startInfo)!);
    }

    public static LeaseHolderProcess Start(params string[] args) => Start(null, args);

    /// <summary>Returns the first output line starting with one of <paramref name="prefixes"/>.</summary>
    public string WaitForLine(params string[] prefixes)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !_lines.TryTake(out var line, remaining))
                throw new TimeoutException($"Lease holder did not print any of: {string.Join(", ", prefixes)}.");
            if (prefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal)))
                return line;
        }
    }

    public int WaitForExit()
    {
        if (!_process.WaitForExit(30_000)) throw new TimeoutException("Lease holder did not exit.");
        return _process.ExitCode;
    }

    /// <summary>Simulates a crash: SIGKILL on Unix, TerminateProcess on Windows.</summary>
    public void Kill()
    {
        _process.Kill();
        _process.WaitForExit(30_000);
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) Kill();
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        _process.Dispose();
        _lines.Dispose();
    }
}
