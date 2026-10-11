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

    /// <exception cref="WorktreeOwnershipConflictException">Another live process owns the worktree.</exception>
    public static WorktreeOwnership Acquire(ExecutionCoordinationDirectory directory, string solutionPath)
    {
        var worktree = WorktreeIdentity.FromSolutionPath(solutionPath);
        var lockPath = Path.Combine(directory.OwnersDirectory, worktree.Key + ".lock");
        var infoPath = Path.Combine(directory.OwnersDirectory, worktree.Key + ".json");

        FileStream? stream;
        try
        {
            stream = ExclusiveFileLock.TryAcquire(lockPath, FileMode.OpenOrCreate);
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

        var owner = new ExecutionOwnerInfo(Environment.ProcessId, Path.GetFullPath(solutionPath), worktree.RootPath, DateTimeOffset.UtcNow);
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
                root.GetProperty("acquiredAt").GetDateTimeOffset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
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
            writer.WriteEndObject();
        }
        File.Move(temp, infoPath, overwrite: true);
    }
}
