using Piston.Engine.Models;

namespace Piston.Engine.Services;

public interface IBuildService
{
    /// <summary>Builds the entire solution.</summary>
    Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct);

    /// <summary>
    /// Builds specific projects when <paramref name="projectPaths"/> is provided,
    /// or the entire solution when <paramref name="projectPaths"/> is null or empty.
    /// </summary>
    Task<BuildResult> BuildAsync(string solutionPath, IReadOnlyList<string>? projectPaths, CancellationToken ct);
}
