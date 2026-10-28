// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Piston.Hosting;
using Xunit;

namespace Piston.Tests.Hosting;

public sealed class DashboardDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-dashboard-{Guid.NewGuid():N}");
    private readonly ExecutionCoordinationDirectory _directory;
    private string OwnersDirectory => Path.Combine(_directory.BaseDirectory, "v1", "owners");

    public DashboardDiscoveryTests()
    {
        _directory = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        _directory.EnsureReady();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Solution(string name)
    {
        var root = Path.Combine(_root, name);
        _ = Directory.CreateDirectory(Path.Combine(root, ".git"));
        var path = Path.Combine(root, "App.slnx");
        File.WriteAllText(path, "<Solution />");
        return path;
    }

    [Fact]
    public void Discovery_UsesOnlyTheCurrentWorktreeAndLiveOwnership()
    {
        var a = Solution("a");
        var b = Solution("b");
        using var owner = WorktreeOwnership.Acquire(_directory, a, "mcp");
        Assert.Equal("disabled", DashboardDiscovery.Inspect(a, _directory).State);
        owner.PublishDashboardUrl("http://127.0.0.1:12345");
        var endpoint = DashboardDiscovery.Inspect(Path.GetDirectoryName(a)!, _directory);
        Assert.Equal("connected", endpoint.State);
        Assert.Equal(owner.Owner.ProcessId, endpoint.ProcessId);
        Assert.Equal(owner.Owner.AcquiredAt, endpoint.AcquiredAt);
        Assert.Equal(owner.Owner.DashboardUrl, endpoint.Url);
        Assert.Equal("disconnected", DashboardDiscovery.Inspect(b, _directory).State);
        var metadata = File.ReadAllText(Path.Combine(OwnersDirectory, $"{owner.Worktree.Key}.json"));
        owner.Dispose();
        File.WriteAllText(Path.Combine(OwnersDirectory, $"{owner.Worktree.Key}.json"), metadata);
        Assert.Equal("disconnected", DashboardDiscovery.Inspect(a, _directory).State);
    }

    [Fact]
    public void EndpointUpdates_AreAtomicAndPreserveOwnership()
    {
        var solution = Solution("updates");
        using var owner = WorktreeOwnership.Acquire(_directory, solution);
        var generation = owner.Owner.AcquiredAt;
        owner.PublishDashboardUrl("http://localhost:5199");
        _ = Assert.Throws<WorktreeOwnershipConflictException>(() =>
            WorktreeOwnership.Acquire(_directory, solution, contentionRetry: TimeSpan.Zero));
        owner.PublishDashboardUrl(null);
        Assert.Equal(generation, owner.Owner.AcquiredAt);
        Assert.Equal("disabled", DashboardDiscovery.Inspect(solution, _directory).State);
        Assert.Empty(Directory.GetFiles(OwnersDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData("http://evil.example:1234")]
    [InlineData("https://127.0.0.1:1234")]
    [InlineData("http://127.0.0.1:1234/path")]
    [InlineData("http://user:pass@127.0.0.1:1234")]
    [InlineData("http://127.0.0.1:1234/?target=x")]
    public void Discovery_RejectsInvalidEndpoints(string url)
    {
        var solution = Solution("invalid");
        using var owner = WorktreeOwnership.Acquire(_directory, solution);
        owner.PublishDashboardUrl(url);
        Assert.Equal("error", DashboardDiscovery.Inspect(solution, _directory).State);
    }

    [Fact]
    public void OlderOwnersWithoutEndpointMetadata_AreExplicitlyUnavailable()
    {
        var solution = Solution("old");
        using var owner = WorktreeOwnership.Acquire(_directory, solution);
        var state = DashboardDiscovery.Inspect(solution, _directory);
        Assert.Equal("disabled", state.State);
        Assert.Contains("older", state.Message);
        Assert.Null(state.Url);
    }
}
