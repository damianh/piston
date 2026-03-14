using Piston.Engine.Models;

namespace Piston.Engine.Services;

public interface ITestProcessPool : IDisposable
{
    int PoolSize { get; }
    int ActiveCount { get; }

    /// <summary>
    /// Runs a single test project, blocking on the pool semaphore until a slot is available.
    /// Returns the result for that project.
    /// </summary>
    Task<ProjectTestResult> RunProjectAsync(
        ProjectTestRequest request,
        CancellationToken ct);

    /// <summary>
    /// Runs multiple test projects concurrently (up to PoolSize).
    /// Reports progress per-project via the callback.
    /// Returns all results when all projects complete.
    /// </summary>
    Task<IReadOnlyList<ProjectTestResult>> RunProjectsAsync(
        IReadOnlyList<ProjectTestRequest> requests,
        Action<ProjectTestResult>? onProjectCompleted,
        CancellationToken ct);
}
