// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Piston.Engine.Execution;

namespace Piston.Engine.Coverage;

/// <summary>Checks that collectors in different outputs cannot modify a linked binary in common.</summary>
internal static class CoverageOutputIsolation
{
    public static bool CanRunConcurrently(
        IEnumerable<string> directories, out string? reason, CancellationToken ct = default)
    {
        try
        {
            var seenDirectories = new HashSet<(uint Device, uint DevicePart, ulong Id)>();
            foreach (var root in directories)
            {
                ct.ThrowIfCancellationRequested();
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.TryPop(out var directory))
                {
                    ct.ThrowIfCancellationRequested();
                    (uint Device, uint DevicePart, ulong Id) identity;
                    if (OperatingSystem.IsLinux())
                    {
                        var status = LinuxStatus(directory);
                        if ((status.Mode & FileTypeMask) != DirectoryType)
                        {
                            reason = $"output is not an ordinary directory: {directory}";
                            return false;
                        }

                        identity = (status.DeviceMajor, status.DeviceMinor, status.Inode);
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        var status = MacStatus(directory, directory: true);
                        if (status.LinkCount != 1)
                        {
                            reason = $"output directory has multiple or unknown links: {directory}";
                            return false;
                        }

                        identity = (status.Device, 0, status.Id);
                    }
                    else if (OperatingSystem.IsWindows())
                    {
                        var status = WindowsDirectoryStatus(directory);
                        if ((status.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint))
                            != (uint)FileAttributes.Directory)
                        {
                            reason = $"output is not an ordinary directory: {directory}";
                            return false;
                        }

                        identity = (status.Volume, 0, ((ulong)status.IndexHigh << 32) | status.IndexLow);
                    }
                    else
                    {
                        reason = "output link checks are unavailable on this operating system";
                        return false;
                    }

                    if (identity.Id == 0 || !seenDirectories.Add(identity))
                    {
                        reason = $"output directory identity is unknown or repeated: {directory}";
                        return false;
                    }

                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        ct.ThrowIfCancellationRequested();
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            reason = $"output contains a symbolic link or reparse point: {entry}";
                            return false;
                        }

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(entry);
                            continue;
                        }

                        if (LinkCount(entry) != 1)
                        {
                            reason = $"output file has multiple or unknown links: {entry}";
                            return false;
                        }
                    }
                }
            }

            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            reason = $"cannot inspect output links: {ex.Message}";
            return false;
        }
    }

    private static uint LinkCount(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            var status = LinuxStatus(path);
            return (status.Mode & FileTypeMask) == RegularFileType ? status.LinkCount : 0;
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacStatus(path, directory: false).LinkCount;
        }

        if (OperatingSystem.IsWindows())
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw NativeError(path);
            }

            return (information.Attributes & (uint)FileAttributes.ReparsePoint) == 0 ? information.LinkCount : 0;
        }

        return 0;
    }

    private static LinuxFileStatus LinuxStatus(string path)
    {
        const uint requested = 0x0001 | 0x0004 | 0x0100; // type, link count, inode
        if (Statx(-100, path, 0x100, requested, out var status) != 0) // AT_FDCWD, AT_SYMLINK_NOFOLLOW
        {
            throw NativeError(path);
        }

        if ((status.Mask & requested) != requested)
        {
            throw new IOException($"The filesystem did not report link and identity information for '{path}'.");
        }

        return status;
    }

    private static MacFileStatus MacStatus(string path, bool directory)
    {
        const uint objectType = 0x00000008;
        const uint device = 0x00000002;
        const uint fileId = 0x02000000;
        const uint linkCount = 0x00000001;
        var attributes = new MacFileSystem.AttributeList
        {
            BitmapCount = 5,
            CommonAttributes = directory ? device | objectType | fileId : objectType,
            DirectoryAttributes = directory ? linkCount : 0,
            FileAttributes = directory ? 0 : linkCount,
        };
        var buffer = new byte[directory ? 24 : 12];
        if (MacFileSystem.GetAttributeList(path, ref attributes, buffer, (nuint)buffer.Length, 1) != 0) // FSOPT_NOFOLLOW
        {
            throw NativeError(path);
        }

        var objectTypeOffset = directory ? 8 : 4;
        if (BitConverter.ToUInt32(buffer, 0) != buffer.Length
            || BitConverter.ToUInt32(buffer, objectTypeOffset) != (directory ? 2u : 1u)) // VDIR or VREG
        {
            return default;
        }

        return directory
            ? new MacFileStatus(BitConverter.ToUInt32(buffer, 4), BitConverter.ToUInt64(buffer, 12), BitConverter.ToUInt32(buffer, 20))
            : new MacFileStatus(0, 0, BitConverter.ToUInt32(buffer, 8));
    }

    private static WindowsFileInformation WindowsDirectoryStatus(string path)
    {
        const uint shareAll = 0x00000007; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
        const uint openExisting = 3;
        const uint flags = 0x02000000 | 0x00200000; // FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT
        using var handle = CreateFile(path, 0, shareAll, IntPtr.Zero, openExisting, flags, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
        {
            throw NativeError(path);
        }

        return information;
    }

    private static IOException NativeError(string path) =>
        new($"Cannot inspect '{path}': {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFileType = 0x8000;
    private const ushort DirectoryType = 0x4000;

    // statx has a fixed 256-byte layout on all Linux architectures.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint LinkCount;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    private readonly record struct MacFileStatus(uint Device, ulong Id, uint LinkCount);

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct WindowsFileInformation
    {
        [FieldOffset(0)] public uint Attributes;
        [FieldOffset(28)] public uint Volume;
        [FieldOffset(40)] public uint LinkCount;
        [FieldOffset(44)] public uint IndexHigh;
        [FieldOffset(48)] public uint IndexLow;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxFileStatus status);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsFileInformation information);
}
