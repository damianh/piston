// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;

namespace Piston.Engine.Services;

/// <summary>Maps test project paths to the suite names their results are reported under.</summary>
internal static class TestProjectNames
{
    internal static bool IsProjectFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj";

    /// <summary>The project's file name without extension (e.g. <c>MyApp.Tests</c>).</summary>
    internal static string SuiteName(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>The project's path relative to the solution directory, with a stable separator.</summary>
    internal static string ProjectId(string solutionDirectory, string projectPath)
    {
        var normalizedDirectory = PathNormalizer.GetFullPath(solutionDirectory);
        var normalizedProjectPath = PathNormalizer.GetFullPath(projectPath, normalizedDirectory);
        if (OperatingSystem.IsWindows()
            && (IsExtendedPath(normalizedDirectory) || IsExtendedPath(normalizedProjectPath)))
        {
            normalizedDirectory = ToExtendedPath(normalizedDirectory);
            normalizedProjectPath = ToExtendedPath(normalizedProjectPath);
        }

        return Path.GetRelativePath(normalizedDirectory, normalizedProjectPath).Replace('\\', '/');
    }

    private static bool IsExtendedPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal);

    private static string ToExtendedPath(string path)
    {
        if (IsExtendedPath(path))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }
}
