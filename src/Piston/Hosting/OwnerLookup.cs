// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Piston.Engine.Execution;

namespace Piston.Hosting;

/// <summary>
/// Finds Piston processes from the machine-wide worktree ownership records, so <c>status</c> and <c>stop</c>
/// work when a daemon's pipe is unreachable or its worktree was deleted, and for <c>piston mcp</c> sessions.
/// </summary>
internal static class OwnerLookup
{
    private const int SigTerm = 15;

    /// <summary>All ownership records; empty when the coordination directory is unusable.</summary>
    public static IReadOnlyList<OwnershipRecord> ListAll(ExecutionCoordinationDirectory? directory = null)
    {
        try
        {
            return WorktreeOwnership.ListOwners(directory ?? ExecutionCoordinationDirectory.ResolveDefault());
        }
        catch (ExecutionCoordinationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Live owners of <paramref name="path"/>: the owner of that exact solution; otherwise, when the path
    /// exists, the owner of the worktree containing it (by <see cref="WorktreeIdentity"/>, so a nested worktree
    /// never matches its parent's owner); otherwise (deleted worktree) the owner with the deepest recorded
    /// worktree path that is or contains it.
    /// </summary>
    public static IReadOnlyList<OwnershipRecord> FindLive(IEnumerable<OwnershipRecord> records, string path)
    {
        var target = Normalize(path);
        var live = records.Where(r => r.Liveness == OwnershipLiveness.Live).ToList();

        var exact = live.Where(r => r.Owner is { } owner && PathEquals(Normalize(owner.SolutionPath), target)).ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        if ((File.Exists(target) || Directory.Exists(target)) && TryGetWorktreeKey(target) is { } key)
        {
            return live.Where(r => string.Equals(r.Key, key, StringComparison.Ordinal)).ToList();
        }

        var containing = live
            .Where(r => r.Owner is { } owner && IsSameOrUnder(target, Normalize(owner.WorktreePath)))
            .ToList();
        if (containing.Count == 0)
        {
            return containing;
        }

        var deepest = containing.Max(r => Normalize(r.Owner!.WorktreePath).Length);
        return containing.Where(r => Normalize(r.Owner!.WorktreePath).Length == deepest).ToList();
    }

    private static string? TryGetWorktreeKey(string path)
    {
        try
        {
            return WorktreeIdentity.FromSolutionPath(path).Key;
        }
        catch (Exception ex) when (ex is ExecutionCoordinationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-checks <paramref name="record"/> against <paramref name="current"/> records immediately before signalling,
    /// so a stale sidecar never leads to terminating an unrelated process that reused the PID.
    /// Returns the process to terminate, or null with <paramref name="reason"/> explaining why it was skipped.
    /// </summary>
    public static Process? VerifyOwnerProcess(OwnershipRecord record, IEnumerable<OwnershipRecord> current, out string reason)
    {
        var fresh = current.FirstOrDefault(r => string.Equals(r.Key, record.Key, StringComparison.Ordinal));
        if (fresh is not { Liveness: OwnershipLiveness.Live, Owner: { } owner } ||
            record.Owner is not { } expected ||
            owner.ProcessId != expected.ProcessId || owner.AcquiredAt != expected.AcquiredAt)
        {
            reason = "the ownership record changed or was released; re-run `piston status` and try again.";
            return null;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(owner.ProcessId);
        }
        catch (ArgumentException)
        {
            reason = $"PID {owner.ProcessId} is no longer running.";
            return null;
        }

        string name;
        DateTime startTime;
        try
        {
            name = process.ProcessName;
            startTime = process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            process.Dispose();
            reason = $"cannot inspect PID {owner.ProcessId} ({ex.Message}).";
            return null;
        }

        if (!IsPlausibleOwner(name, startTime, owner, out reason))
        {
            process.Dispose();
            return null;
        }

        return process;
    }

    /// <summary>Clock skew tolerated between a process's reported start time and its recorded acquisition.</summary>
    internal static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True when a process named <paramref name="processName"/> started at <paramref name="startTimeUtc"/> can be the
    /// recorded owner: it is piston or dotnet, and it started no later than it acquired ownership.
    /// </summary>
    internal static bool IsPlausibleOwner(string processName, DateTime startTimeUtc, ExecutionOwnerInfo owner, out string reason)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        if (!name.StartsWith("piston", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"PID {owner.ProcessId} is '{processName}', not piston/dotnet; the PID was probably reused.";
            return false;
        }

        if (startTimeUtc > owner.AcquiredAt.UtcDateTime + StartTimeTolerance)
        {
            reason = $"PID {owner.ProcessId} started at {startTimeUtc:u}, after ownership was acquired at " +
                     $"{owner.AcquiredAt:u}; the PID was probably reused.";
            return false;
        }

        reason = "";
        return true;
    }

    public static string Describe(OwnershipRecord record)
    {
        if (record.Owner is not { } owner)
        {
            return $"unknown process (ownership key {record.Key}; details unavailable)";
        }

        var missing = Directory.Exists(owner.WorktreePath) ? "" : " [worktree missing]";
        return $"PID {owner.ProcessId} ({owner.Host ?? "daemon"}) | solution {owner.SolutionPath} | " +
               $"worktree {owner.WorktreePath}{missing} | since {owner.AcquiredAt:u}";
    }

    /// <summary>
    /// Asks <paramref name="process"/> to exit (SIGTERM on Unix), then kills its process tree after
    /// <paramref name="gracePeriod"/>. Returns true when the process is gone. Disposes <paramref name="process"/>.
    /// </summary>
    public static async Task<bool> TerminateAsync(Process process, TimeSpan gracePeriod)
    {
        using (process)
        {
            if (process.HasExited)
            {
                return true;
            }

            if (!OperatingSystem.IsWindows() && Kill(process.Id, SigTerm) == 0 && await WaitAsync(process, gracePeriod).ConfigureAwait(false))
            {
                return true;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                return true; // exited
            }

            return await WaitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task<bool> WaitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison Comparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool PathEquals(string a, string b) => string.Equals(a, b, Comparison);

    private static bool IsSameOrUnder(string path, string root) =>
        PathEquals(path, root) || path.StartsWith(root + Path.DirectorySeparatorChar, Comparison);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);
}
