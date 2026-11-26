// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>Captures what a hung test's processes were doing before its run is cancelled.</summary>
internal interface IHangDiagnostics
{
    Task<HangDiagnosticsResult?> CaptureAsync(HangCapture capture, CancellationToken ct);
}

/// <param name="ProjectPath">The test project.</param>
/// <param name="Test">The hung test's result key.</param>
/// <param name="Method">The hung test's method (Namespace.Class.Method), when the runner reported it.</param>
/// <param name="Processes">The processes started for the project's run.</param>
internal sealed record HangCapture(string ProjectPath, string Test, string? Method, IReadOnlyList<OwnedProcess> Processes);

/// <param name="Stacks">The stacks that reference the hung test, for the test's stack trace.</param>
/// <param name="Attachments">The full stack report and any process dumps, moved into the artifact store when retained.</param>
internal sealed record HangDiagnosticsResult(string? Stacks, IReadOnlyList<TestAttachment> Attachments);

/// <summary>
/// Reads the managed stacks of a hung test's .NET processes with ClrMD and writes a full-heap dump of the process that
/// runs the test. A process is inspected when it runs in the job or process group of one of the run's processes, and
/// is relevant when a stack or async state machine references the test's class, or failing that, when it has loaded
/// the test assembly. When a process cannot be attached to, it is dumped and its stacks are read from the dump.
/// </summary>
internal sealed class ClrMdHangDiagnostics : IHangDiagnostics
{
    internal const int MaxDumpsPerCapture = 2;
    internal const int MaxStackTraceLength = 32 * 1024;
    private const int MaxFramesPerThread = 100;
    private const int MaxStateMachines = 50;

    private readonly string _stagingDirectory;
    private readonly Func<IEnumerable<int>> _publishedProcesses;
    private readonly bool _writeDumps;

    public ClrMdHangDiagnostics(string stagingDirectory, bool writeDumps = true, Func<IEnumerable<int>>? publishedProcesses = null)
    {
        _stagingDirectory = stagingDirectory;
        _writeDumps = writeDumps;
        _publishedProcesses = publishedProcesses ?? DiagnosticsClient.GetPublishedProcesses;
    }

    public async Task<HangDiagnosticsResult?> CaptureAsync(HangCapture capture, CancellationToken ct)
    {
        CleanStaging();
        var candidates = _publishedProcesses()
            .Where(pid => pid != Environment.ProcessId && capture.Processes.Any(p => p.ContainsProcess(pid)))
            .Distinct()
            .ToList();
        if (candidates.Count == 0)
        {
            Log($"No .NET processes found for {capture.Test}");
            return null;
        }

        var target = TestTarget.From(capture.Method, Path.GetFileNameWithoutExtension(capture.ProjectPath));
        var reports = new List<ProcessReport>();
        foreach (var pid in candidates)
        {
            ct.ThrowIfCancellationRequested();
            reports.Add(await Task.Run(() => Inspect(pid, target, () => DataTarget.AttachToProcess(pid, suspend: true), ct), ct)
                .ConfigureAwait(false));
        }

        var attachments = new List<TestAttachment>();
        if (_writeDumps)
        {
            var toDump = reports.Where(r => r.Relevance > 0).OrderByDescending(r => r.Relevance)
                .Concat(reports.Where(r => r.Error is not null))
                .Take(MaxDumpsPerCapture)
                .ToList();
            foreach (var report in toDump)
            {
                var dump = await WriteDumpAsync(report, capture, ct).ConfigureAwait(false);
                if (dump is null)
                {
                    continue;
                }

                if (report.Error is not null)
                {
                    var fromDump = await Task.Run(() => Inspect(report.ProcessId, target, () => DataTarget.LoadDump(dump), ct), ct)
                        .ConfigureAwait(false);
                    reports[reports.IndexOf(report)] = fromDump with { Source = "dump" };
                    if (fromDump.Relevance == 0 && fromDump.Error is null)
                    {
                        TryDelete(dump);
                        continue;
                    }
                }

                attachments.Add(new TestAttachment(dump, $"hang-{report.ProcessId}.dmp",
                    $"Full-heap dump of process {report.ProcessId} when {capture.Test} was cancelled")
                {
                    MoveOnRetain = true,
                });
            }
        }

        var full = FormatReport(capture, reports, relevantOnly: false);
        var stacksFile = Path.Combine(_stagingDirectory, $"hang-stacks-{Guid.NewGuid():N}.txt");
        try
        {
            _ = Directory.CreateDirectory(_stagingDirectory);
            await File.WriteAllTextAsync(stacksFile, full, ct).ConfigureAwait(false);
            var stacks = new TestAttachment(
                stacksFile,
                "hang-stacks.txt",
                $"Managed stacks of every thread when {capture.Test} was cancelled")
            {
                MoveOnRetain = true,
            };
            attachments.Insert(0, stacks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Cannot write the hang stack report: {ex.Message}");
        }

        var relevant = FormatReport(capture, reports, relevantOnly: true);
        return new HangDiagnosticsResult(
            relevant.Length > MaxStackTraceLength ? relevant[..MaxStackTraceLength] + "\n… (truncated; see hang-stacks.txt)" : relevant,
            attachments);
    }

    private static ProcessReport Inspect(int pid, TestTarget target, Func<DataTarget> open, CancellationToken ct)
    {
        try
        {
            using var dataTarget = open();
            if (dataTarget.ClrVersions.IsDefaultOrEmpty)
            {
                return new ProcessReport(pid, ProcessName(pid), [], [], 0, "no CLR found");
            }

            using var runtime = dataTarget.ClrVersions[0].CreateRuntime();
            var threads = new List<ThreadReport>();
            foreach (var thread in runtime.Threads)
            {
                if (!thread.IsAlive)
                {
                    continue;
                }

                var frames = new List<string>();
                var references = false;
                foreach (var frame in thread.EnumerateStackTrace().Take(MaxFramesPerThread))
                {
                    if (frame.Method is { } method)
                    {
                        references |= target.Matches(method.Type?.Name);
                        frames.Add(method.Signature ?? $"{method.Type?.Name}.{method.Name}");
                    }
                    else if (!string.IsNullOrEmpty(frame.FrameName))
                    {
                        frames.Add($"[{frame.FrameName}]");
                    }
                }

                if (frames.Count > 0)
                {
                    threads.Add(new ThreadReport(thread.ManagedThreadId, thread.OSThreadId, frames, references));
                }
            }

            var stateMachines = FindStateMachines(runtime, target, ct);
            var relevance = threads.Any(t => t.ReferencesTest) || stateMachines.Count > 0 ? 2
                : target.AssemblyName is { } assembly && runtime.EnumerateModules().Any(m =>
                    string.Equals(Path.GetFileNameWithoutExtension(m.Name), assembly, StringComparison.OrdinalIgnoreCase)) ? 1
                : 0;
            return new ProcessReport(pid, ProcessName(pid), threads, stateMachines, relevance, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProcessReport(pid, ProcessName(pid), [], [], 0, ex.Message);
        }
    }

    // An awaiting async test has no thread on its stack; its state machine on the heap shows where it is suspended.
    private static List<string> FindStateMachines(ClrRuntime runtime, TestTarget target, CancellationToken ct)
    {
        var found = new List<string>();
        if (target.StateMachinePrefix is not { } prefix || !runtime.Heap.CanWalkHeap)
        {
            return found;
        }

        var seen = 0;
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            if (++seen % 4096 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (obj.Type?.Name is { } name && name.StartsWith(prefix, StringComparison.Ordinal))
            {
                var state = obj.Type.GetFieldByName("<>1__state") is { } field ? field.Read<int>(obj.Address, interior: false).ToString() : "?";
                found.Add($"{name} (state {state}; -1 running, -2 finished, 0 or more awaiting)");
                if (found.Count >= MaxStateMachines)
                {
                    break;
                }
            }
        }

        return found;
    }

    private async Task<string?> WriteDumpAsync(ProcessReport report, HangCapture capture, CancellationToken ct)
    {
        var path = Path.Combine(_stagingDirectory, $"hang-{report.ProcessId}-{Guid.NewGuid():N}.dmp");
        try
        {
            _ = Directory.CreateDirectory(_stagingDirectory);
            var started = Stopwatch.GetTimestamp();
            await new DiagnosticsClient(report.ProcessId)
                .WriteDumpAsync(DumpType.WithHeap, path, logDumpGeneration: false, ct)
                .ConfigureAwait(false);
            Log($"Wrote a dump of process {report.ProcessId} for {capture.Test} in {Durations.Format(Stopwatch.GetElapsedTime(started))}");
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Cannot dump process {report.ProcessId}: {ex.Message}");
            TryDelete(path);
            return null;
        }
    }

    private static string FormatReport(HangCapture capture, IReadOnlyList<ProcessReport> reports, bool relevantOnly)
    {
        var sb = new StringBuilder();
        if (!relevantOnly)
        {
            _ = sb.AppendLine($"Hang diagnostics for {capture.Test} ({DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz})").AppendLine();
        }

        var processes = relevantOnly ? reports.Where(r => r.Relevance > 0).ToList() : [.. reports];
        foreach (var report in processes)
        {
            var anyReference = report.Threads.Any(t => t.ReferencesTest);
            var threads = relevantOnly && anyReference ? report.Threads.Where(t => t.ReferencesTest) : report.Threads;
            _ = sb.AppendLine($"Process {report.ProcessId}{(report.Name is null ? "" : $" ({report.Name})")}{(report.Source is null ? "" : $" [from {report.Source}]")}:");
            if (report.Error is not null)
            {
                _ = sb.AppendLine($"  Cannot read stacks: {report.Error}");
            }

            foreach (var stateMachine in report.StateMachines)
            {
                _ = sb.AppendLine($"  Async state machine {stateMachine}");
            }

            foreach (var thread in threads)
            {
                _ = sb.AppendLine($"  Thread {thread.ManagedId} (OS thread {thread.OsId}):");
                foreach (var frame in thread.Frames)
                {
                    _ = sb.AppendLine($"    at {frame}");
                }
            }

            _ = sb.AppendLine();
        }

        if (relevantOnly && processes.Count == 0)
        {
            _ = sb.AppendLine(reports.All(r => r.Error is not null)
                ? $"Cannot read the stacks of the test's processes: {reports[0].Error}"
                : "No managed thread referenced the test; see hang-stacks.txt for every thread.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string? ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private void CleanStaging()
    {
        try
        {
            if (!Directory.Exists(_stagingDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_stagingDirectory))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Cannot clean {_stagingDirectory}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Cannot delete {path}: {ex.Message}");
        }
    }

    private static void Log(string message) => DiagnosticLog.Instance?.Write("Timeout", message);

    private sealed record ThreadReport(int ManagedId, uint OsId, IReadOnlyList<string> Frames, bool ReferencesTest);

    private sealed record ProcessReport(
        int ProcessId,
        string? Name,
        IReadOnlyList<ThreadReport> Threads,
        IReadOnlyList<string> StateMachines,
        int Relevance,
        string? Error)
    {
        public string? Source { get; init; }
    }

    /// <summary>The hung test's class (and its nested types) and async state machine, as ClrMD names them.</summary>
    internal sealed record TestTarget(string? ClassName, string? StateMachinePrefix, string? AssemblyName)
    {
        public static TestTarget From(string? method, string? assemblyName)
        {
            var name = method?.Split('(')[0].Trim();
            var dot = name?.LastIndexOf('.') ?? -1;
            if (name is null || dot <= 0 || dot == name.Length - 1)
            {
                return new TestTarget(null, null, assemblyName);
            }

            var className = name[..dot];
            return new TestTarget(className, $"{className}+<{name[(dot + 1)..]}>d__", assemblyName);
        }

        public bool Matches(string? typeName) =>
            ClassName is { } className && typeName is not null
            && (string.Equals(typeName, className, StringComparison.Ordinal)
                || typeName.StartsWith(className + "+", StringComparison.Ordinal));
    }
}
