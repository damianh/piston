// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class MtpDiscoveryCacheTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _directory = Directory.CreateTempSubdirectory("piston-discovery-").FullName;
    private readonly string _appPath;
    private readonly MtpDiscoveryCache _cache = new();

    public MtpDiscoveryCacheTests()
    {
        _appPath = Path.Combine(_directory, "Tests.dll");
        File.WriteAllText(_appPath, "app");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ReusesSuccessfulDiscovery_OnlyForTheSameOutputAndWorkingDirectory()
    {
        var calls = 0;
        Task<MtpDiscoveryCache.Discovery> Discover(CancellationToken _) =>
            Task.FromResult(Success((++calls).ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var first = await _cache.GetAsync(_appPath, _directory, Discover, _ct);
        var cached = await _cache.GetAsync(_appPath, _directory, Discover, _ct);
        Assert.False(first.Cached);
        Assert.True(cached.Cached);
        Assert.Equal("1", Assert.Single(cached.Discovery.Nodes).GetProperty("uid").GetString());
        Assert.Equal(1, calls);

        await File.WriteAllTextAsync(Path.Combine(_directory, "dependency.dll"), "dependency", _ct);
        Assert.False((await _cache.GetAsync(_appPath, _directory, Discover, _ct)).Cached);
        await File.AppendAllTextAsync(_appPath, "changed", _ct);
        Assert.False((await _cache.GetAsync(_appPath, _directory, Discover, _ct)).Cached);
        _cache.Invalidate();
        Assert.False((await _cache.GetAsync(_appPath, _directory, Discover, _ct)).Cached);
        var otherDirectory = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;
        Assert.False((await _cache.GetAsync(_appPath, otherDirectory, Discover, _ct)).Cached);
        Assert.Equal(5, calls);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    public async Task FailedOrCancelledDiscovery_IsNotCached(string failure)
    {
        var result = failure switch
        {
            "failed" => new MtpServerSession.Result(MtpServerSession.Outcome.Failed, null, []),
            "cancelled" => new MtpServerSession.Result(MtpServerSession.Outcome.Cancelled, null, []),
            "rejected" => new MtpServerSession.Result(MtpServerSession.Outcome.Completed, "rejected", []),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var calls = 0;
        Task<MtpDiscoveryCache.Discovery> Discover(CancellationToken _) =>
            Task.FromResult(++calls == 1
                ? new MtpDiscoveryCache.Discovery(result, [])
                : Success("ok"));

        Assert.False((await _cache.GetAsync(_appPath, _directory, Discover, _ct)).Cached);
        Assert.False((await _cache.GetAsync(_appPath, _directory, Discover, _ct)).Cached);
        Assert.True((await _cache.GetAsync(_appPath, _directory, Discover, _ct)).Cached);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneDiscovery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<MtpDiscoveryCache.Discovery> Discover(CancellationToken token)
        {
            _ = Interlocked.Increment(ref calls);
            _ = started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Success("shared");
        }

        var lookups = Enumerable.Range(0, 8).Select(_ => _cache.GetAsync(_appPath, _directory, Discover, _ct)).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();

        var results = await Task.WhenAll(lookups).WaitAsync(TimeSpan.FromSeconds(10), _ct);
        Assert.Equal(1, calls);
        _ = Assert.Single(results, result => !result.Cached);
        Assert.All(results, result => Assert.Equal("shared", Assert.Single(result.Discovery.Nodes).GetProperty("uid").GetString()));
    }

    [Fact]
    public async Task CancellingAWaitingCaller_DoesNotCancelTheDiscovery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<MtpDiscoveryCache.Discovery> Discover(CancellationToken token)
        {
            _ = Interlocked.Increment(ref calls);
            started.SetResult();
            await release.Task.WaitAsync(token);
            return Success("shared");
        }

        var first = _cache.GetAsync(_appPath, _directory, Discover, _ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var waiting = _cache.GetAsync(_appPath, _directory, Discover, stop.Token);
        await stop.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult();

        Assert.False((await first.WaitAsync(TimeSpan.FromSeconds(10), _ct)).Cached);
        Assert.True((await _cache.GetAsync(_appPath, _directory, Discover, _ct)).Cached);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellingTheDiscoveringCaller_AllowsAnotherCallerToRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<MtpDiscoveryCache.Discovery> Discover(CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return Success("retry");
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var first = _cache.GetAsync(_appPath, _directory, Discover, stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        var second = _cache.GetAsync(_appPath, _directory, Discover, _ct);
        await stop.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Assert.False((await second.WaitAsync(TimeSpan.FromSeconds(10), _ct)).Cached);
        Assert.True((await _cache.GetAsync(_appPath, _directory, Discover, _ct)).Cached);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task InvalidatedDuringDiscovery_DoesNotCacheTheOldResult()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<MtpDiscoveryCache.Discovery> Discover(CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
            }

            return Success("result");
        }

        var first = _cache.GetAsync(_appPath, _directory, Discover, _ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        _cache.Invalidate();
        release.SetResult();
        _ = await first.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.False((await _cache.GetAsync(_appPath, _directory, Discover, _ct)).Cached);
        Assert.Equal(2, calls);
    }

    private static MtpDiscoveryCache.Discovery Success(string uid)
    {
        using var json = JsonDocument.Parse($$"""{"uid":"{{uid}}"}""");
        return new MtpDiscoveryCache.Discovery(
            new MtpServerSession.Result(MtpServerSession.Outcome.Completed, null, []), [json.RootElement.Clone()]);
    }
}
