// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Hosting.Services;

/// <summary>
/// Reads source files for the dashboard, e.g. the file a stack frame names. Only text files under the solution
/// directory are served, after resolving symbolic links, so a request can't read anything else on the machine.
/// </summary>
internal static class SourceFileReader
{
    internal const long MaxBytes = 1024 * 1024;

    private const int BinarySniffBytes = 8000;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static SourceFileDto Read(string? requestedPath, string? solutionDirectory)
    {
        if (string.IsNullOrWhiteSpace(requestedPath) || solutionDirectory is null)
        {
            return new SourceFileDto(requestedPath ?? string.Empty, null, "No file was requested.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath, solutionDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SourceFileDto(requestedPath, null, "The path is not valid.");
        }

        try
        {
            var root = Resolve(Path.GetFullPath(solutionDirectory));
            if (!IsUnder(Resolve(fullPath), root))
            {
                return new SourceFileDto(fullPath, null, "The file is outside the solution directory.");
            }

            var info = new FileInfo(Resolve(fullPath));
            if (!info.Exists)
            {
                return new SourceFileDto(fullPath, null, "The file no longer exists.");
            }

            if (info.Length > MaxBytes)
            {
                return new SourceFileDto(fullPath, null, "The file is too large to show.");
            }

            var bytes = File.ReadAllBytes(info.FullName);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, BinarySniffBytes)).Contains((byte)0))
            {
                return new SourceFileDto(fullPath, null, "The file is not text.");
            }

            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return new SourceFileDto(fullPath, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SourceFileDto(fullPath, null, "The file could not be read.");
        }
    }

    internal static bool IsUnder(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(directory);
        return path.Length > root.Length
            && path.StartsWith(root, PathComparison)
            && (path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar);
    }

    // Resolves symbolic links in every segment, so a link inside the solution can't point outside it.
    private static string Resolve(string path)
    {
        var parent = Path.GetDirectoryName(path);
        var resolvedParent = parent is null || parent == path ? parent : Resolve(parent);
        var current = resolvedParent is null ? path : Path.Combine(resolvedParent, Path.GetFileName(path));
        FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
        return info.LinkTarget is null
            ? current
            : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName is { } target ? Resolve(target) : current;
    }
}
