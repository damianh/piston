// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

internal static class ProjectLabels
{
    public static string For(string projectPath, IEnumerable<string> projectPaths, string? solutionPath)
    {
        projectPath = projectPath.Replace('\\', '/');
        var shortName = Path.GetFileNameWithoutExtension(projectPath);
        if (!projectPaths.Select(path => path.Replace('\\', '/')).Any(path =>
                !string.Equals(path, projectPath, StringComparison.Ordinal)
                && string.Equals(Path.GetFileNameWithoutExtension(path), shortName, StringComparison.Ordinal)))
        {
            return shortName;
        }

        var solutionDirectory = solutionPath is null ? null : Path.GetDirectoryName(solutionPath.Replace('\\', '/'));
        var projectId = solutionDirectory is null ? projectPath : Path.GetRelativePath(solutionDirectory, projectPath);
        return $"{shortName} ({projectId.Replace('\\', '/')})";
    }
}
