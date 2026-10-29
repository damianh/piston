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
                !string.Equals(path, projectPath, ProjectPaths.Comparison(projectPath))
                && string.Equals(Path.GetFileNameWithoutExtension(path), shortName, StringComparison.Ordinal)))
        {
            return shortName;
        }

        var projectId = solutionPath is null ? projectPath : ProjectPaths.ProjectId(projectPath, solutionPath);
        return $"{shortName} ({projectId.Replace('\\', '/')})";
    }
}
