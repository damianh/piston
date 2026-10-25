// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;

namespace Piston.Engine.Execution;

/// <summary>
/// Exclusive ownership of a worktree's execution by one daemon process. Held as an exclusive lock on
/// <c>owners/&lt;key&gt;.lock</c>, which the OS releases when the process exits for any reason, so a
/// crashed owner never blocks a new daemon. Lock files are never deleted (deleting would let two
/// processes lock different inodes of the same path); the diagnostic sidecar is deleted on release.
/// </summary>
public sealed class WorktreeOwnership : IDisposable
{
    private readonly FileStream _lock;
    private readonly string _infoPath;
    private int _disposed;

    private WorktreeOwnership(WorktreeIdentity worktree, ExecutionOwnerInfo owner, FileStream @lock, string infoPath)
    {
        Worktree = worktree;
        Owner = owner;
        _lock = @lock;
        _infoPath = infoPath;
    }

    public WorktreeIdentity Worktree { get; }
    public ExecutionOwnerInfo Owner { get; }

    /// <summary>
    /// How long <see cref="Acquire"/> retries a held lock before reporting a conflict. Covers the brief
    /// window in which <see cref="ListOwners"/> (<c>status</c>/<c>stop</c>) probes an unheld lock.
    /// </summary>
    public static readonly TimeSpan DefaultContentionRetry = TimeSpan.FromMilliseconds(250);

    /// <param name="host">Recorded as <see cref="ExecutionOwnerInfo.Host"/> (for example <c>daemon</c> or <c>mcp</c>).</param>
    /// <param name="contentionRetry">Retry window while the lock is held; defaults to <see cref="DefaultContentionRetry"/>.</param>
    /// <exception cref="WorktreeOwnershipConflictException">Another live process owns the worktree.</exception>
    public static WorktreeOwnership Acquire(
        ExecutionCoordinationDirectory directory, string solutionPath, string? host = null,
        TimeSpan? contentionRetry = null, string? dashboardUrl = null)
    {
        if (dashboardUrl is not null && !TryValidateDashboardUrl(dashboardUrl, out dashboardUrl))
        {
            throw new ArgumentException("Dashboard URL must be an HTTP URL on localhost or a loopback IP.", nameof(dashboardUrl));
        }

        var worktree = WorktreeIdentity.FromSolutionPath(solutionPath);
        var lockPath = Path.Combine(directory.OwnersDirectory, worktree.Key + ".lock");
        var infoPath = Path.Combine(directory.OwnersDirectory, worktree.Key + ".json");

        FileStream? stream;
        var deadline = DateTime.UtcNow + (contentionRetry ?? DefaultContentionRetry);
        try
        {
            while ((stream = ExclusiveFileLock.TryAcquire(lockPath, FileMode.OpenOrCreate)) is null && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutionCoordinationException(
                $"Cannot acquire worktree ownership lock '{lockPath}': {ex.Message}", ex);
        }

        if (stream is null)
        {
            throw new WorktreeOwnershipConflictException(worktree, TryReadInfo(infoPath));
        }

        var owner = new ExecutionOwnerInfo(
            Environment.ProcessId, Path.GetFullPath(solutionPath), worktree.RootPath, DateTimeOffset.UtcNow, host, dashboardUrl);
        try
        {
            WriteInfo(infoPath, owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream.Dispose();
            throw new ExecutionCoordinationException(
                $"Cannot record worktree owner details '{infoPath}': {ex.Message}", ex);
        }

        return new WorktreeOwnership(worktree, owner, stream, infoPath);
    }

    /// <summary>
    /// Lists ownership records in <paramref name="directory"/>, probing each lock without blocking.
    /// A probe briefly takes an unheld lock; <see cref="Acquire"/> retries briefly to ride over it.
    /// </summary>
    public static IReadOnlyList<OwnershipRecord> ListOwners(ExecutionCoordinationDirectory directory)
    {
        string[] locks;
        try
        {
            locks = Directory.GetFiles(directory.OwnersDirectory, "*.lock");
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var records = new List<OwnershipRecord>();
        foreach (var lockPath in locks)
        {
            var key = Path.GetFileNameWithoutExtension(lockPath);
            var infoPath = Path.Combine(directory.OwnersDirectory, key + ".json");
            var liveness = ProbeLock(lockPath);
            if (liveness == OwnershipLiveness.None)
            {
                continue;
            }

            var info = TryReadInfo(infoPath);
            if (liveness == OwnershipLiveness.Stale && info is null)
            {
                // A released lock without details is the normal idle state, not a stale record.
                continue;
            }

            records.Add(new OwnershipRecord(key, liveness, info));
        }
        return records;
    }

    private static OwnershipLiveness ProbeLock(string lockPath)
    {
        FileStream? stream;
        try
        {
            stream = ExclusiveFileLock.TryAcquire(lockPath, FileMode.Open);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return OwnershipLiveness.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OwnershipLiveness.None;
        }

        if (stream is null)
        {
            return OwnershipLiveness.Live;
        }

        stream.Dispose();
        return OwnershipLiveness.Stale;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            // Delete before unlocking so it never removes the next owner's details.
            File.Delete(_infoPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostic only; the next owner overwrites it.
        }
        _lock.Dispose();
    }

    internal static ExecutionOwnerInfo? TryReadInfo(string infoPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(infoPath));
            var root = document.RootElement;
            return new ExecutionOwnerInfo(
                root.GetProperty("processId").GetInt32(),
                root.GetProperty("solutionPath").GetString() ?? "",
                root.GetProperty("worktreePath").GetString() ?? "",
                root.GetProperty("acquiredAt").GetDateTimeOffset(),
                root.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.String ? host.GetString() : null,
                root.TryGetProperty("dashboardUrl", out var dashboardUrl)
                && dashboardUrl.ValueKind == JsonValueKind.String
                && TryValidateDashboardUrl(dashboardUrl.GetString(), out var safeDashboardUrl)
                    ? safeDashboardUrl
                    : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool TryValidateDashboardUrl(string? value, out string safeUrl)
    {
        safeUrl = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !uri.IsLoopback
            || uri.UserInfo.Length > 0
            || (uri.AbsolutePath is not "" and not "/")
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return false;
        }

        safeUrl = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static void WriteInfo(string infoPath, ExecutionOwnerInfo owner)
    {
        var temp = $"{infoPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        using (var file = File.Create(temp))
        using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("processId", owner.ProcessId);
            writer.WriteString("solutionPath", owner.SolutionPath);
            writer.WriteString("worktreePath", owner.WorktreePath);
            writer.WriteString("acquiredAt", owner.AcquiredAt);
            if (owner.Host is not null)
            {
                writer.WriteString("host", owner.Host);
            }
            if (owner.DashboardUrl is not null)
            {
                writer.WriteString("dashboardUrl", owner.DashboardUrl);
            }
            writer.WriteEndObject();
        }
        File.Move(temp, infoPath, overwrite: true);
    }
}

/// <summary>Whether an ownership lock is held by a live process.</summary>
public enum OwnershipLiveness
{
    /// <summary>No lock file.</summary>
    None,

    /// <summary>The lock is held: <see cref="OwnershipRecord.Owner"/> describes a running process.</summary>
    Live,

    /// <summary>The lock is free but owner details remain (the owner crashed or was killed).</summary>
    Stale,
}

/// <summary>An ownership record found by <see cref="WorktreeOwnership.ListOwners"/>.</summary>
/// <param name="Owner">Recorded details; null when a live owner's details could not be read.</param>
public sealed record OwnershipRecord(string Key, OwnershipLiveness Liveness, ExecutionOwnerInfo? Owner);
