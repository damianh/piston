// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

public interface ITestProcessPool : IDisposable
{
    int PoolSize { get; }
    int ActiveCount { get; }

    /// <summary>
    /// Waits for a pool slot and holds it until the returned handle is disposed, so test processes
    /// started outside <see cref="RunProjectsAsync(IReadOnlyList{ProjectTestRequest}, TestRunCallbacks, CancellationToken)"/>
    /// (discovery, single-project runs) share the same capacity. Throws
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled while waiting.
    /// </summary>
    Task<IDisposable> AcquireSlotAsync(CancellationToken ct);

    /// <summary>
    /// Runs a single test project, blocking on the pool semaphore until a slot is available.
    /// Returns the result for that project.
    /// </summary>
    Task<ProjectTestResult> RunProjectAsync(
        ProjectTestRequest request,
        CancellationToken ct);

    /// <summary>
    /// Runs a single test project once a pool slot is available, reporting its start (once it holds a
    /// slot) and live progress via <paramref name="callbacks"/>. Completion is left to the caller.
    /// </summary>
    Task<ProjectTestResult> RunProjectAsync(
        ProjectTestRequest request,
        TestRunCallbacks callbacks,
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

    /// <summary>
    /// Runs multiple test projects concurrently (up to PoolSize), reporting each project's
    /// start (once it holds a slot), live progress, and completion via <paramref name="callbacks"/>.
    /// </summary>
    Task<IReadOnlyList<ProjectTestResult>> RunProjectsAsync(
        IReadOnlyList<ProjectTestRequest> requests,
        TestRunCallbacks callbacks,
        CancellationToken ct);
}
