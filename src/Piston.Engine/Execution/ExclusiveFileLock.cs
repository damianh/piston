// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>Verifies that a directory's filesystem enforces the locks used for process coordination.</summary>
public static class FileLocking
{
    public static void VerifyEnforced(string directory)
    {
        var probePath = Path.Combine(directory, $"lock-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var created = false;
        try
        {
            using var first = ExclusiveFileLock.TryAcquire(probePath, FileMode.CreateNew)
                ?? throw new IOException($"Could not lock new probe file '{probePath}'.");
            created = true;
            using var second = ExclusiveFileLock.TryAcquire(probePath, FileMode.Open);
            if (second is not null)
            {
                throw new IOException(
                    $"Exclusive file locks are not enforced in '{directory}'. Unset DOTNET_SYSTEM_IO_DISABLEFILELOCKING " +
                    "and the System.IO.DisableFileLocking switch, or use a local filesystem.");
            }
        }
        finally
        {
            if (created)
            {
                File.Delete(probePath);
            }
        }
    }
}

/// <summary>
/// Exclusive file locks held through an open <see cref="FileStream"/> with <see cref="FileShare.None"/>.
/// Windows enforces this with sharing modes; on Unix .NET applies <c>flock(LOCK_EX)</c>, which is per open
/// file description, so two handles conflict even within one process. The OS releases both on process
/// exit, including crashes, which is what makes stale-owner recovery reliable. Handles are opened
/// close-on-exec/non-inheritable, so build and test child processes do not keep locks alive.
/// </summary>
internal static class ExclusiveFileLock
{
    // flock reports EWOULDBLOCK; .NET surfaces errno as the IOException HResult on Unix.
    private const int LinuxEWouldBlock = 11;
    private const int BsdEWouldBlock = 35;
    private const int WindowsSharingViolation = 32;
    private const int WindowsLockViolation = 33;

    /// <summary>Returns the held stream, or null when another handle holds the lock.</summary>
    /// <exception cref="FileNotFoundException">With <see cref="FileMode.Open"/>, when the file is gone.</exception>
    public static FileStream? TryAcquire(string path, FileMode mode, FileOptions options = FileOptions.None)
    {
        try
        {
            return new FileStream(path, mode, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, options);
        }
        catch (IOException ex) when (IsContention(ex))
        {
            return null;
        }
    }

    public static async Task<FileStream> AcquireAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            var stream = TryAcquire(path, FileMode.OpenOrCreate);
            if (stream is not null)
            {
                return stream;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes an existing lock file if no live handle holds it. Returns false when it is held.
    /// </summary>
    public static bool TryRemoveUnheld(string path)
    {
        // On Unix the delete happens while holding the lock; DeleteOnClose is only used on Windows,
        // where a failed open never yields a handle that could delete a live holder's file.
        var options = OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None;
        FileStream? stream;
        try
        {
            stream = TryAcquire(path, FileMode.Open, options);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }

        if (stream is null)
        {
            return false;
        }

        using (stream)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(path);
            }
        }
        return true;
    }

    internal static bool IsContention(IOException ex)
    {
        if (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return (ex.HResult & 0xFFFF) is WindowsSharingViolation or WindowsLockViolation;
        }

        var expected = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? BsdEWouldBlock : LinuxEWouldBlock;
        return ex.HResult == expected;
    }
}
