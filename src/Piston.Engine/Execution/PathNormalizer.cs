// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>Normalizes filesystem paths before identity comparisons and MSBuild execution.</summary>
public static class PathNormalizer
{
    public static string GetFullPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(path);
        }

        string ordinary;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            ordinary = @"\\" + path[8..];
        }
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)
            && path.Length >= 7 && char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\')
        {
            ordinary = path[4..];
        }
        else
        {
            return Path.GetFullPath(path);
        }

        // Converting namespace-dependent names would address a different filesystem entry or device.
        var rootLength = Path.GetPathRoot(ordinary)!.Length;
        var literalNames = ordinary[..rootLength].Split('\\', '/').Any(HasLiteralEnding)
            || ordinary[rootLength..].Split('\\', '/').Any(RequiresExtendedNamespace);
        // Modern .NET/MSBuild accept ordinary long paths, which also resolve dot segments correctly.
        return Path.GetFullPath(literalNames ? path : ordinary);
    }

    public static string GetFullPath(string path, string basePath) =>
        GetFullPath(Path.GetFullPath(Path.IsPathFullyQualified(path) ? GetFullPath(path) : path, GetFullPath(basePath)));

    /// <summary>Chooses a process-startable directory without changing dotnet's global.json selection.</summary>
    public static string GetSdkWorkingDirectory(string path)
    {
        var full = GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return full;
        }

        // CreateProcess rejects long working directories even with an extended prefix. Builds use
        // absolute targets, so an ancestor is safe provided it selects the same SDK configuration.
        while (full.Length >= 259)
        {
            if (File.Exists(Path.Combine(full, "global.json")))
            {
                throw new PathTooLongException(
                    $"Cannot start dotnet in '{path}' without changing SDK selection: '{full}' contains global.json. Use a shorter checkout path.");
            }
            full = PathNormalizer.GetFullPath(Path.GetDirectoryName(full)
                ?? throw new PathTooLongException($"Cannot find a process working directory for '{path}'."));
        }
        return full;
    }

    private static bool RequiresExtendedNamespace(string segment)
    {
        if (segment is "." or "..")
        {
            return false;
        }
        if (HasLiteralEnding(segment))
        {
            return true;
        }
        var name = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
                && (name[3] is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3'));
    }

    private static bool HasLiteralEnding(string segment) => segment.EndsWith('.') || segment.EndsWith(' ');
}
