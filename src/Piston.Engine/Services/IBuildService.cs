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
}
