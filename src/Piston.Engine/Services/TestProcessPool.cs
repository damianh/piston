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
        if (poolSize < 1) throw new ArgumentOutOfRangeException(nameof(poolSize), "Pool size must be at least 1.");
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
    public async Task<ProjectTestResult> RunProjectAsync(
        ProjectTestRequest request,
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

        Interlocked.Increment(ref _activeCount);
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
            return await _runDelegate(request, null, ct).ConfigureAwait(false);
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
            Interlocked.Decrement(ref _activeCount);
            _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProjectTestResult>> RunProjectsAsync(
        IReadOnlyList<ProjectTestRequest> requests,
        Action<ProjectTestResult>? onProjectCompleted,
        CancellationToken ct)
    {
        var tasks = requests.Select(req => RunAndReport(req, onProjectCompleted, ct)).ToList();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ProjectTestResult> RunAndReport(
        ProjectTestRequest request,
        Action<ProjectTestResult>? onProjectCompleted,
        CancellationToken ct)
    {
        var result = await RunProjectAsync(request, ct).ConfigureAwait(false);
        onProjectCompleted?.Invoke(result);
        return result;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _semaphore.Dispose();
    }
}
