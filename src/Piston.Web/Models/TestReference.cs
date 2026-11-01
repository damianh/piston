// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>A test result key scoped to its project identity, independent of the host's project path separators.</summary>
public sealed record TestReference(string Project, string Key)
{
    public static TestReference FromProjectPath(string projectPath, string key, string? solutionPath = null) =>
        new(solutionPath is null
            ? Path.GetFileNameWithoutExtension(projectPath.Replace('\\', '/'))
            : ProjectPaths.ProjectId(projectPath, solutionPath), key);
}
