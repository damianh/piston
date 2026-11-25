// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Piston;

/// <summary>
/// Copies the installed tool into a per-build runtime directory so the .NET tool store holds no open files
/// and <c>dotnet tool update</c> can replace the installed version while Piston processes keep running.
/// </summary>
internal static class RuntimeStaging
{
    internal const string Command = "__stage-runtime";
    internal const string Marker = ".piston-runtime";
    internal const string LastUsed = ".last-used";
    internal const string Lease = ".lease";
    private const string StagingSuffix = ".staging";
    private const string DeletingSuffix = ".deleting";
    private const string LockFile = ".lock";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static FileStream? _activeLease;

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
    /// Replaces the tool-store process with the staged copy on Unix. A process already running from a
    /// staged copy holds a lease so cleanup can distinguish it from an unused runtime.
    /// </summary>
    internal static int? RunFromInstalledPackage(IReadOnlyList<string> args, TextWriter error)
    {
        var source = Path.GetFullPath(AppContext.BaseDirectory);
        if (IsComplete(source))
        {
            try
            {
                _activeLease ??= AcquireLease(source);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"piston: cannot claim the runtime copy '{source}': {ex.Message}");
                return 1;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var runtimeIdPath = Path.Combine(source, "piston.runtime-id");
        if (!File.Exists(runtimeIdPath))
        {
            return null;
        }

        try
        {
            var runtimeId = File.ReadAllText(runtimeIdPath).Trim();
            if (runtimeId is "" or "." or ".."
                || Path.GetFileName(runtimeId) != runtimeId
                || runtimeId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error.WriteLine($"piston: invalid runtime identity in '{runtimeIdPath}'.");
                return 1;
            }

            var destination = Path.Combine(GetRuntimeRoot(), runtimeId);
            _ = Stage(source, destination);
            Cleanup(Path.GetDirectoryName(destination)!, Path.GetFileName(destination), DateTime.UtcNow);
            ExecStaged(destination, args);
            error.WriteLine("piston: could not replace the tool-store process with its runtime copy.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            error.WriteLine($"piston: cannot run from a runtime copy: {ex.Message}");
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
            File.WriteAllBytes(Path.Combine(staging, Lease), []);
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

    internal static FileStream AcquireLease(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var root = Path.GetDirectoryName(directory)
            ?? throw new ArgumentException($"'{directory}' has no parent directory.", nameof(directory));
        using (AcquireLock(root))
        {
            if (!IsComplete(directory))
            {
                throw new IOException($"'{directory}' is not a complete runtime copy.");
            }

            MarkUsed(directory);
            var leasePath = Path.Combine(directory, Lease);
            if (!File.Exists(leasePath))
            {
                File.WriteAllBytes(leasePath, []);
            }

            return new FileStream(leasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
    }

    /// <summary>
    /// Removes runtimes other than <paramref name="current"/> that have not been started for
    /// <see cref="UnusedRetention"/>. On Unix an exclusive lease is required before removal because the OS
    /// allows deleting files from a directory that a running process still uses. Windows protects loaded files
    /// by refusing the rename.
    /// </summary>
    internal static void Cleanup(string root, string current, DateTime utcNow)
    {
        if (!Directory.Exists(root))
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

                FileStream? lease = null;
                try
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        lease = new FileStream(Path.Combine(directory, Lease), FileMode.OpenOrCreate,
                            FileAccess.ReadWrite, FileShare.None);
                    }

                    var deleting = $"{directory}.{Guid.NewGuid():N}{DeletingSuffix}";
                    Directory.Move(directory, deleting);
                    removed.Add(deleting);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                finally
                {
                    lease?.Dispose();
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

        MarkUsed(directory);
        return true;
    }

    private static void MarkUsed(string directory)
    {
        File.WriteAllBytes(Path.Combine(directory, LastUsed), []);
        File.SetLastWriteTimeUtc(Path.Combine(directory, LastUsed), DateTime.UtcNow);
    }

    private static string GetRuntimeRoot()
    {
        var dataRoot = Environment.GetEnvironmentVariable("PISTON_RUNTIMES");
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Path.GetTempPath();
        }

        return Path.Combine(dataRoot, "Piston", "runtimes");
    }

    private static void ExecStaged(string destination, IReadOnlyList<string> args)
    {
        var executable = Environment.ProcessPath
            ?? throw new IOException("The current dotnet executable path is unavailable.");
        var stagedAssembly = Path.Combine(destination, "Piston.dll");
        var arguments = new string[args.Count + 2];
        arguments[0] = executable;
        arguments[1] = stagedAssembly;
        for (var i = 0; i < args.Count; i++)
        {
            arguments[i + 2] = args[i];
        }

        var argumentVector = Marshal.AllocHGlobal((arguments.Length + 1) * IntPtr.Size);
        var allocatedArguments = new IntPtr[arguments.Length];
        var path = IntPtr.Zero;
        try
        {
            path = Marshal.StringToCoTaskMemUTF8(executable);
            for (var i = 0; i < arguments.Length; i++)
            {
                allocatedArguments[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
                Marshal.WriteIntPtr(argumentVector, i * IntPtr.Size, allocatedArguments[i]);
            }

            Marshal.WriteIntPtr(argumentVector, arguments.Length * IntPtr.Size, IntPtr.Zero);
            _ = NativeMethods.ExecV(path, argumentVector);
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "execv failed.");
        }
        finally
        {
            if (path != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(path);
            }

            foreach (var argument in allocatedArguments)
            {
                if (argument != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(argument);
                }
            }

            Marshal.FreeHGlobal(argumentVector);
        }
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

    private static partial class NativeMethods
    {
        [DllImport("libc", EntryPoint = "execv", SetLastError = true)]
        internal static extern int ExecV(IntPtr path, IntPtr argv);
    }
}
