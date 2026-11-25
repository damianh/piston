// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston;

/// <summary>
/// Copies the installed tool into a per-build runtime directory for the Windows launcher (piston.cmd).
/// Piston then runs from that copy, so the .NET tool store holds no open files and
/// <c>dotnet tool update</c> can replace the installed version while Piston processes keep running.
/// </summary>
internal static class RuntimeStaging
{
    internal const string Command = "__stage-runtime";
    internal const string Marker = ".piston-runtime";
    internal const string LastUsed = ".last-used";
    private const string StagingSuffix = ".staging";
    private const string DeletingSuffix = ".deleting";
    private const string LockFile = ".lock";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Runtimes unused for this long are removed when another runtime is staged.</summary>
    internal static readonly TimeSpan UnusedRetention = TimeSpan.FromDays(7);

    private static readonly TimeSpan AbandonedStagingAge = TimeSpan.FromHours(1);

    /// <summary>Runs the hidden <c>__stage-runtime &lt;source&gt; &lt;destination&gt;</c> command.</summary>
    internal static int Run(IReadOnlyList<string> args, TextWriter error)
    {
        if (args.Count != 2)
        {
            error.WriteLine($"piston: usage: piston {Command} <source> <destination>");
            return 2;
        }

        try
        {
            var destination = Stage(args[0], args[1]);
            Cleanup(Path.GetDirectoryName(destination)!, Path.GetFileName(destination), DateTime.UtcNow);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine($"piston: cannot prepare the runtime copy '{args[1]}' of '{args[0]}': {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Returns <paramref name="destination"/> as a complete copy of <paramref name="source"/>, marked as just
    /// used. The copy is made under a unique sibling name and renamed into place, so a concurrent start never
    /// runs a partial copy and a destination with a marker is always complete. Checking, marking, repairing and
    /// publishing happen under the runtimes lock that <see cref="Cleanup"/> also takes, so cleanup never removes
    /// a runtime a launcher has just been given.
    /// </summary>
    internal static string Stage(string source, string destination)
    {
        source = Path.GetFullPath(source);
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var root = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException($"'{destination}' has no parent directory.", nameof(destination));
        _ = Directory.CreateDirectory(root);
        using (AcquireLock(root))
        {
            if (TryClaim(destination))
            {
                return destination;
            }
        }

        if (!File.Exists(Path.Combine(source, "Piston.dll")))
        {
            throw new IOException($"'{source}' does not contain Piston.dll.");
        }

        var staging = $"{destination}.{Guid.NewGuid():N}{StagingSuffix}";
        try
        {
            CopyDirectory(source, staging);
            File.WriteAllText(Path.Combine(staging, Marker), source);
            using (AcquireLock(root))
            {
                // A concurrent start may have staged the same build while this one copied.
                if (TryClaim(destination))
                {
                    return destination;
                }

                // Never completed (no marker), so nothing runs from it.
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, recursive: true);
                }

                Directory.Move(staging, destination);
                _ = TryClaim(destination);
                return destination;
            }
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// Removes runtimes other than <paramref name="current"/> that have not been started for
    /// <see cref="UnusedRetention"/>. A runtime is first renamed away: Windows refuses to rename a directory
    /// while a running Piston has its assemblies loaded, so a runtime in use is never touched.
    /// </summary>
    internal static void Cleanup(string root, string current, DateTime utcNow)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(root))
        {
            return;
        }

        var removed = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.EndsWith(DeletingSuffix, StringComparison.Ordinal))
            {
                removed.Add(directory);
            }
            else if (name.EndsWith(StagingSuffix, StringComparison.Ordinal))
            {
                if (utcNow - Directory.GetLastWriteTimeUtc(directory) > AbandonedStagingAge)
                {
                    removed.Add(directory);
                }
            }
            else if (IsComplete(directory))
            {
                // Rechecked under the lock: a launcher claims a runtime under the same lock before it starts.
                using var held = AcquireLock(root);
                if (utcNow - LastUsedUtc(directory) <= UnusedRetention)
                {
                    continue;
                }

                var deleting = $"{directory}.{Guid.NewGuid():N}{DeletingSuffix}";
                try
                {
                    Directory.Move(directory, deleting);
                    removed.Add(deleting);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        foreach (var directory in removed)
        {
            TryDelete(directory);
        }
    }

    private static bool IsComplete(string directory) => File.Exists(Path.Combine(directory, Marker));

    /// <summary>Marks a complete runtime as just used; returns false when it is not complete.</summary>
    private static bool TryClaim(string directory)
    {
        if (!IsComplete(directory))
        {
            return false;
        }

        File.WriteAllBytes(Path.Combine(directory, LastUsed), []);
        File.SetLastWriteTimeUtc(Path.Combine(directory, LastUsed), DateTime.UtcNow);
        return true;
    }

    /// <summary>Serializes staging and cleanup across processes; the OS releases it if a process dies.</summary>
    private static FileStream AcquireLock(string root)
    {
        var path = Path.Combine(root, LockFile);
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static DateTime LastUsedUtc(string directory)
    {
        var lastUsed = Path.Combine(directory, LastUsed);
        return File.Exists(lastUsed)
            ? File.GetLastWriteTimeUtc(lastUsed)
            : File.GetLastWriteTimeUtc(Path.Combine(directory, Marker));
    }

    private static void CopyDirectory(string source, string destination)
    {
        _ = Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            _ = Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retried by a later cleanup.
        }
    }
}
