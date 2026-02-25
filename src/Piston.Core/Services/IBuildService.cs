using Piston.Core.Models;

namespace Piston.Core.Services;

public interface IBuildService
{
    Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct);
}
