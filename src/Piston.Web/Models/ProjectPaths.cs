// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

internal static class ProjectPaths
{
    public static StringComparison Comparison(string path) =>
        WindowsRootLength(path.Replace('\\', '/')) > 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string ProjectId(string projectPath, string solutionPath)
    {
        projectPath = projectPath.Replace('\\', '/');
        solutionPath = solutionPath.Replace('\\', '/');
        var rootLength = WindowsRootLength(solutionPath);
        if (rootLength == 0)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
            return Path.GetRelativePath(directory, Path.GetFullPath(projectPath, directory)).Replace('\\', '/');
        }

        // The browser uses Unix path rules even when the daemon is on Windows.
        var project = projectPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var solutionDirectory = solutionPath.Split('/', StringSplitOptions.RemoveEmptyEntries)[..^1];
        if (rootLength != WindowsRootLength(projectPath)
            || !project.Take(rootLength).SequenceEqual(solutionDirectory.Take(rootLength), StringComparer.OrdinalIgnoreCase))
        {
            return projectPath;
        }

        var common = rootLength;
        while (common < project.Length && common < solutionDirectory.Length
            && string.Equals(project[common], solutionDirectory[common], StringComparison.OrdinalIgnoreCase))
        {
            common++;
        }

        var relative = string.Join('/', Enumerable.Repeat("..", solutionDirectory.Length - common).Concat(project.Skip(common)));
        return relative.Length == 0 ? "." : relative;
    }

    private static int WindowsRootLength(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/' ? 1
        : path.StartsWith("//", StringComparison.Ordinal) ? 2 : 0;
}
