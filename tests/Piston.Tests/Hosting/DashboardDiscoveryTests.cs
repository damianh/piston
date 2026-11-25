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
    public void Discovery_IgnoresInvalidEndpointsFromOtherAndStaleOwners()
    {
        var current = Solution("current");
        var other = Solution("other");
        using var owner = WorktreeOwnership.Acquire(_directory, current);
        using var otherOwner = WorktreeOwnership.Acquire(_directory, other);
        owner.PublishDashboardUrl("http://127.0.0.1:12345");
        otherOwner.PublishDashboardUrl("http://localhost:1234");
        var infoPath = Path.Combine(OwnersDirectory, $"{otherOwner.Worktree.Key}.json");
        File.WriteAllText(infoPath, File.ReadAllText(infoPath)
            .Replace("http://localhost:1234", "http://evil.example:1234", StringComparison.Ordinal));
        Assert.Equal("connected", DashboardDiscovery.Inspect(current, _directory).State);
        Assert.Equal("error", DashboardDiscovery.Inspect(other, _directory).State);

        var metadata = File.ReadAllText(infoPath);
        otherOwner.Dispose();
        File.WriteAllText(infoPath, metadata);
        Assert.Equal("connected", DashboardDiscovery.Inspect(current, _directory).State);
        Assert.Equal("disconnected", DashboardDiscovery.Inspect(other, _directory).State);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Discovery_WindowsPathForms_FindTheSameOwner(bool extendedOwner)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var solution = Solution("windows");
        var extended = @"\\?\" + solution;
        using var owner = WorktreeOwnership.Acquire(_directory, extendedOwner ? extended : solution, "daemon");
        owner.PublishDashboardUrl("http://localhost:5199");

        var ordinaryState = DashboardDiscovery.Inspect(Path.GetDirectoryName(solution)!, _directory);
        var extendedState = DashboardDiscovery.Inspect(Path.GetDirectoryName(extended)!, _directory);
        Assert.Equal("connected", ordinaryState.State);
        Assert.Equal(ordinaryState, extendedState);
        Assert.Equal(owner.Worktree.RootPath, extendedState.Worktree);
        Assert.Equal(owner.Owner.ProcessId, extendedState.ProcessId);
        Assert.Equal(owner.Owner.AcquiredAt, extendedState.AcquiredAt);
    }

    [Fact]
    public void Disconnected_SuggestsConfiguredSolutionOrExplainsResolutionFailure()
    {
        var root = Path.Combine(_root, "nested");
        var nested = Directory.CreateDirectory(Path.Combine(root, "apps")).FullName;
        var solution = Path.Combine(nested, "App.slnx");
        File.WriteAllText(solution, "<Solution />");
        File.WriteAllText(Path.Combine(root, ".piston.json"), """{ "solution": "apps/App.slnx" }""");

        var configured = DashboardDiscovery.Inspect(root, _directory);
        Assert.Equal("disconnected", configured.State);
        Assert.Equal(solution, configured.Solution);
        Assert.Null(configured.SolutionError);

        File.WriteAllText(Path.Combine(root, ".piston.json"), """{ "solution": "missing.slnx" }""");
        var missing = DashboardDiscovery.Inspect(root, _directory);
        Assert.Equal("disconnected", missing.State);
        Assert.Null(missing.Solution);
        Assert.Contains("missing.slnx", missing.SolutionError);
    }

    [Fact]
    public void Discovery_WindowsExtendedWorktree_FindsOrdinaryPathOwner()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var solution = Solution("extended");
        using var owner = WorktreeOwnership.Acquire(_directory, solution, "mcp");
        var endpoint = DashboardDiscovery.Inspect(@"\\?\" + Path.GetDirectoryName(solution)!, _directory);
        Assert.Equal("disabled", endpoint.State);
        Assert.Equal(owner.Worktree.RootPath, endpoint.Worktree);
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
        Assert.Contains("restart", state.Message);
        Assert.Null(state.Url);
    }
}
