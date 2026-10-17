// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

public interface IBuildService
{
    /// <summary>Builds the entire solution.</summary>
    Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct);

    /// <summary>
    /// Builds specific projects when <paramref name="projectPaths"/> is provided,
    /// or the entire solution when <paramref name="projectPaths"/> is null or empty.
    /// When <paramref name="solutionPath"/> is a readable .sln/.slnx/.slnf, the projects that
    /// belong to it are built in a single invocation through a temporary solution filter
    /// (their ProjectReferences are built too); other projects are built individually.
    /// </summary>
    Task<BuildResult> BuildAsync(string solutionPath, IReadOnlyList<string>? projectPaths, CancellationToken ct);

    /// <summary>
    /// As <see cref="BuildAsync(string, IReadOnlyList{string}?, CancellationToken)"/>, invoking
    /// <paramref name="onProjectBuilt"/> with each project's name as its build output is produced.
    /// </summary>
    Task<BuildResult> BuildAsync(
        string solutionPath,
        IReadOnlyList<string>? projectPaths,
        Action<string>? onProjectBuilt,
        CancellationToken ct) =>
        BuildAsync(solutionPath, projectPaths, ct);
}
