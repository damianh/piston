// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Hosting;
using Xunit;

namespace Piston.Tests.Hosting;

public sealed class WorktreePresenceMonitorTests
{
    private bool _solutionExists = true;
    private bool _rootExists = true;
    private readonly List<string> _fired = [];

    private WorktreePresenceMonitor Create() => new("/w/App.slnx", "/w", _fired.Add,
        fileExists: _ => _solutionExists, directoryExists: _ => _rootExists);

    [Fact]
    public void Fires_AfterTwoConsecutiveMisses_Once()
    {
        using var monitor = Create();
        Assert.False(monitor.Check());

        _solutionExists = false;
        _rootExists = false;
        Assert.False(monitor.Check());
        Assert.True(monitor.Check());
        Assert.False(monitor.Check());

        Assert.Equal(["/w"], _fired);
    }

    [Fact]
    public void DoesNotFire_WhenOnlySolutionMissing()
    {
        using var monitor = Create();
        _solutionExists = false;
        for (var i = 0; i < 5; i++)
        {
            Assert.False(monitor.Check());
        }

        Assert.Empty(_fired);
    }

    [Fact]
    public void TransientMiss_ResetsCount()
    {
        using var monitor = Create();
        _solutionExists = false;
        _rootExists = false;
        Assert.False(monitor.Check());

        _rootExists = true;
        Assert.False(monitor.Check());

        _rootExists = false;
        Assert.False(monitor.Check());
        Assert.True(monitor.Check());
    }

    [Fact]
    public async Task Timer_DetectsRealDeletion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"piston-presence-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(root);
        var solution = Path.Combine(root, "App.slnx");
        File.WriteAllText(solution, "<Solution />");
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new WorktreePresenceMonitor(solution, root, r => fired.TrySetResult(r),
            interval: TimeSpan.FromMilliseconds(50));
        monitor.Start();

        await Task.Delay(200);
        Assert.False(fired.Task.IsCompleted);

        Directory.Delete(root, recursive: true);
        Assert.Equal(root, await fired.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
