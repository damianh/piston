using Piston.Engine.Models;

namespace Piston.Engine.Services;

public interface IBuildService
{
    Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct);
}
