// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Runtime.InteropServices;

namespace Piston.Engine.Execution;

internal static class MacFileSystem
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct AttributeList
    {
        public ushort BitmapCount, Reserved;
        public uint CommonAttributes, VolumeAttributes, DirectoryAttributes, FileAttributes, ForkAttributes;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getattrlist", SetLastError = true)]
    internal static extern int GetAttributeList(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref AttributeList attributes,
        [Out] byte[] buffer, nuint bufferSize, nuint options);
}
