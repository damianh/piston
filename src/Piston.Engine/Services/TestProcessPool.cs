// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Semaphore-based concurrency limiter for parallel <c>dotnet test</c> invocations.
/// Bounds how many test projects run simultaneously and provides per-project error isolation.
/// </summary>
public sealed class TestProcessPool : ITestProcessPool
{
    private readonly SemaphoreSlim _semaphore;
    private readonly int _recycleAfter;
    private readonly Func<ProjectTestRequest, Action<IReadOnlyList<Engine.Models.TestSuite>>?, CancellationToken, Task<ProjectTestResult>> _runDelegate;
    private int _activeCount;
    private int _totalRunCount;
    private bool _disposed;

    /// <summary>
    /// Creates a pool backed by the supplied <see cref="ITestExecutionStrategy"/>.
    /// </summary>
    public TestProcessPool(int poolSize, int recycleAfter, ITestExecutionStrategy strategy)
        : this(poolSize, recycleAfter,
            (req, onProgress, ct) => strategy.ExecuteAsync(req, onProgress, ct))
    {
    }

    /// <summary>
    /// Creates a pool with a custom run delegate (for testing).
    /// </summary>
    internal TestProcessPool(
        int poolSize,
        int recycleAfter,
        Func<ProjectTestRequest, Action<IReadOnlyList<Engine.Models.TestSuite>>?, CancellationToken, Task<ProjectTestResult>> runDelegate)
    {
        if (poolSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(poolSize), "Pool size must be at least 1.");
        }

        PoolSize = poolSize;
        _recycleAfter = recycleAfter;
        _semaphore = new SemaphoreSlim(poolSize, poolSize);
        _runDelegate = runDelegate;
    }

    /// <inheritdoc/>
    public int PoolSize { get; }

    /// <inheritdoc/>
    public int ActiveCount => _activeCount;

    /// <inheritdoc/>
    public Task<ProjectTestResult> RunProjectAsync(
        ProjectTestRequest request,
        CancellationToken ct) =>
        RunProjectAsync(request, new TestRunCallbacks(), ct);

    private async Task<ProjectTestResult> RunProjectAsync(
        ProjectTestRequest request,
        TestRunCallbacks callbacks,
        CancellationToken ct)
    {
        try
        {
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ProjectTestResult(request.ProjectPath, [], null, [], Crashed: false);
        }

        _ = Interlocked.Increment(ref _activeCount);
        var runCount = Interlocked.Increment(ref _totalRunCount);

        if (runCount > 0 && runCount % _recycleAfter == 0)
        {
            // Log a warning when a slot has served many runs (no persistent process to recycle,
            // but this is the hook point for future cleanup logic)
            System.Diagnostics.Debug.WriteLine(
                $"[TestProcessPool] Slot has served {runCount} total runs (recycleAfter={_recycleAfter}). " +
                "Consider clearing temp state if needed.");
        }

        try
        {
            callbacks.OnProjectStarted?.Invoke(request.ProjectPath);
            Action<IReadOnlyList<Engine.Models.TestSuite>>? onProgress = callbacks.OnProjectProgress is { } onProjectProgress
                ? suites => onProjectProgress(request.ProjectPath, suites)
                : null;
            return await _runDelegate(request, onProgress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ProjectTestResult(request.ProjectPath, [], null, [], Crashed: false);
        }
        catch (Exception ex)
        {
            return new ProjectTestResult(request.ProjectPath, [], ex.Message, [], Crashed: true);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _activeCount);
            _ = _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ProjectTestResult>> RunProjectsAsync(
        IReadOnlyList<ProjectTestRequest> requests,
        Action<ProjectTestResult>? onProjectCompleted,
        CancellationToken ct) =>
        RunProjectsAsync(requests, new TestRunCallbacks { OnProjectCompleted = onProjectCompleted }, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProjectTestResult>> RunProjectsAsync(
        IReadOnlyList<ProjectTestRequest> requests,
        TestRunCallbacks callbacks,
        CancellationToken ct)
    {
        var tasks = requests.Select(req => RunAndReportAsync(req, callbacks, ct)).ToList();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ProjectTestResult> RunAndReportAsync(
        ProjectTestRequest request,
        TestRunCallbacks callbacks,
        CancellationToken ct)
    {
        var result = await RunProjectAsync(request, callbacks, ct).ConfigureAwait(false);
        callbacks.OnProjectCompleted?.Invoke(result);
        return result;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _semaphore.Dispose();
    }
}
