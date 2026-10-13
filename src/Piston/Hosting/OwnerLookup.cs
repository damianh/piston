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
    /// Live owners whose solution is <paramref name="path"/>, or whose worktree is or contains it.
    /// </summary>
    public static IReadOnlyList<OwnershipRecord> FindLive(IEnumerable<OwnershipRecord> records, string path)
    {
        var target = Normalize(path);
        return records
            .Where(r => r.Liveness == OwnershipLiveness.Live && r.Owner is { } owner &&
                        (PathEquals(Normalize(owner.SolutionPath), target) || IsSameOrUnder(target, Normalize(owner.WorktreePath))))
            .ToList();
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
    /// Asks <paramref name="pid"/> to exit (SIGTERM on Unix), then kills its process tree after
    /// <paramref name="gracePeriod"/>. Returns true when the process is gone.
    /// </summary>
    public static async Task<bool> TerminateAsync(int pid, TimeSpan gracePeriod)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            if (!OperatingSystem.IsWindows() && Kill(pid, SigTerm) == 0 && await WaitAsync(process, gracePeriod).ConfigureAwait(false))
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
