using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TestProcessPoolTests
{
    /// <summary>
    /// Creates a pool with a fake delegate that records concurrent invocations.
    /// The delegate simulates work with a configurable delay and returns a result.
    /// </summary>
    private static TestProcessPool CreatePool(
        int poolSize,
        Func<ProjectTestRequest, CancellationToken, Task<ProjectTestResult>> workFunc)
    {
        return new TestProcessPool(
            poolSize,
            recycleAfter: 50,
            runDelegate: (req, _, ct) => workFunc(req, ct));
    }

    [Fact]
    public void PoolSize_ReturnsConfiguredSize()
    {
        using var pool = CreatePool(3, (req, ct) => Task.FromResult(
            new ProjectTestResult(req.ProjectPath, [], null, [], false)));

        Assert.Equal(3, pool.PoolSize);
    }

    [Fact]
    public async Task RunProjectAsync_ReturnsCrashedResult_OnException()
    {
        using var pool = CreatePool(2, (_, _) => throw new InvalidOperationException("boom"));

        var request = new ProjectTestRequest("proj.csproj", null, false);
        var result = await pool.RunProjectAsync(request, CancellationToken.None);

        Assert.True(result.Crashed);
        Assert.Equal("boom", result.RunnerError);
    }

    [Fact]
    public async Task RunProjectAsync_ReturnsCancelledResult_OnCancellation()
    {
        using var pool = CreatePool(1, async (_, ct) =>
        {
            await Task.Delay(10_000, ct); // will be cancelled
            return new ProjectTestResult("proj.csproj", [], null, [], false);
        });

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var request = new ProjectTestRequest("proj.csproj", null, false);
        var result = await pool.RunProjectAsync(request, cts.Token);

        Assert.False(result.Crashed); // cancelled is not crashed
    }

    [Fact]
    public async Task RunProjectsAsync_AllProjectsComplete()
    {
        var paths = new[] { "a.csproj", "b.csproj", "c.csproj" };

        using var pool = CreatePool(2, (req, ct) =>
            Task.FromResult(new ProjectTestResult(req.ProjectPath, [], null, [], false)));

        var requests = paths.Select(p => new ProjectTestRequest(p, null, false)).ToList();
        var results = await pool.RunProjectsAsync(requests, null, CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.All(paths, p => Assert.Contains(results, r => r.ProjectPath == p));
    }

    [Fact]
    public async Task RunProjectsAsync_BoundsConcurrency_ByPoolSize()
    {
        const int poolSize = 2;
        const int projectCount = 5;
        var maxConcurrent = 0;
        var currentConcurrent = 0;
        var lockObj = new object();

        using var pool = CreatePool(poolSize, async (req, ct) =>
        {
            int current;
            lock (lockObj)
            {
                current = ++currentConcurrent;
                if (current > maxConcurrent)
                    maxConcurrent = current;
            }

            await Task.Delay(50, ct); // hold the slot briefly

            lock (lockObj) { currentConcurrent--; }

            return new ProjectTestResult(req.ProjectPath, [], null, [], false);
        });

        var requests = Enumerable.Range(0, projectCount)
            .Select(i => new ProjectTestRequest($"proj{i}.csproj", null, false))
            .ToList();

        await pool.RunProjectsAsync(requests, null, CancellationToken.None);

        Assert.True(maxConcurrent <= poolSize,
            $"Max concurrent ({maxConcurrent}) exceeded pool size ({poolSize}).");
        Assert.True(maxConcurrent > 0);
    }

    [Fact]
    public async Task RunProjectsAsync_PerProjectCallback_FiresForEachProject()
    {
        var completedPaths = new System.Collections.Concurrent.ConcurrentBag<string>();

        using var pool = CreatePool(2, (req, ct) =>
            Task.FromResult(new ProjectTestResult(req.ProjectPath, [], null, [], false)));

        var requests = new[]
        {
            new ProjectTestRequest("a.csproj", null, false),
            new ProjectTestRequest("b.csproj", null, false),
            new ProjectTestRequest("c.csproj", null, false),
        }.ToList();

        await pool.RunProjectsAsync(requests, r => completedPaths.Add(r.ProjectPath), CancellationToken.None);

        Assert.Equal(3, completedPaths.Count);
        Assert.Contains("a.csproj", completedPaths);
        Assert.Contains("b.csproj", completedPaths);
        Assert.Contains("c.csproj", completedPaths);
    }

    [Fact]
    public async Task RunProjectsAsync_CrashedProject_DoesNotAffectSiblings()
    {
        using var pool = CreatePool(2, (req, ct) =>
        {
            if (req.ProjectPath == "crash.csproj")
                throw new InvalidOperationException("project crash");

            return Task.FromResult(new ProjectTestResult(req.ProjectPath, [], null, [], false));
        });

        var requests = new[]
        {
            new ProjectTestRequest("crash.csproj", null, false),
            new ProjectTestRequest("healthy.csproj", null, false),
        }.ToList();

        var results = await pool.RunProjectsAsync(requests, null, CancellationToken.None);

        Assert.Equal(2, results.Count);

        var crashResult = results.First(r => r.ProjectPath == "crash.csproj");
        Assert.True(crashResult.Crashed);
        Assert.Equal("project crash", crashResult.RunnerError);

        var healthyResult = results.First(r => r.ProjectPath == "healthy.csproj");
        Assert.False(healthyResult.Crashed);
    }

    [Fact]
    public async Task RunProjectsAsync_AllProjectsComplete_WithConcurrentCrashes()
    {
        var paths = new[] { "crash1.csproj", "crash2.csproj", "healthy1.csproj", "healthy2.csproj" };

        using var pool = CreatePool(4, (req, ct) =>
        {
            if (req.ProjectPath.StartsWith("crash", StringComparison.Ordinal))
                throw new Exception("crash");

            return Task.FromResult(new ProjectTestResult(req.ProjectPath, [], null, [], false));
        });

        var requests = paths.Select(p => new ProjectTestRequest(p, null, false)).ToList();
        var results = await pool.RunProjectsAsync(requests, null, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.Equal(2, results.Count(r => r.Crashed));
        Assert.Equal(2, results.Count(r => !r.Crashed));
    }

    [Fact]
    public async Task ActiveCount_TracksInFlightRequests()
    {
        var activeCountDuringRun = 0;
        TestProcessPool? pool = null;

        pool = CreatePool(2, async (req, ct) =>
        {
            if (req.ProjectPath == "slow.csproj")
            {
                activeCountDuringRun = pool!.ActiveCount;
                await Task.Delay(100, ct);
            }
            return new ProjectTestResult(req.ProjectPath, [], null, [], false);
        });
        using var _ = pool;

        var requests = new[]
        {
            new ProjectTestRequest("slow.csproj", null, false),
            new ProjectTestRequest("fast.csproj", null, false),
        }.ToList();

        await pool.RunProjectsAsync(requests, null, CancellationToken.None);

        // At least one was active when we checked
        Assert.True(activeCountDuringRun >= 1);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var pool = CreatePool(1, (req, ct) =>
            Task.FromResult(new ProjectTestResult(req.ProjectPath, [], null, [], false)));

        pool.Dispose();
        pool.Dispose(); // should not throw
    }
}
