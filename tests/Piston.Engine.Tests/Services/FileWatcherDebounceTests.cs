// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class FileWatcherDebounceTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public void MultipleRapidChanges_FiresOnlyOnce()
    {
        var debounce = TimeSpan.FromMilliseconds(100);
        var time = new TestTimeProvider();
        using var sut = new FileWatcherService(debounce, time);
        var fired = new List<FileChangeBatch>();
        sut.FileChanged += e => fired.Add(e);

        for (var i = 0; i < 5; i++)
        {
            sut.ScheduleDebounce(new("Test.cs", WatcherChangeTypes.Changed, time.GetUtcNow()));
            time.Advance(TimeSpan.FromMilliseconds(10));
        }

        time.Advance(TimeSpan.FromMilliseconds(89));
        Assert.Empty(fired);
        time.Advance(TimeSpan.FromMilliseconds(1));
        _ = Assert.Single(fired);
        time.Advance(debounce * 2);
        _ = Assert.Single(fired);
    }

    [Fact]
    public void MultipleDistinctFiles_BatchContainsAllUniqueFiles()
    {
        var debounce = TimeSpan.FromMilliseconds(150);
        var time = new TestTimeProvider();
        using var sut = new FileWatcherService(debounce, time);
        var batches = new List<FileChangeBatch>();
        sut.FileChanged += b => batches.Add(b);

        sut.ScheduleDebounce(new("Alpha.cs", WatcherChangeTypes.Created, time.GetUtcNow()));
        time.Advance(TimeSpan.FromMilliseconds(10));
        sut.ScheduleDebounce(new("Beta.cs", WatcherChangeTypes.Created, time.GetUtcNow()));
        time.Advance(TimeSpan.FromMilliseconds(10));
        sut.ScheduleDebounce(new("Gamma.cs", WatcherChangeTypes.Created, time.GetUtcNow()));
        time.Advance(debounce);

        var batch = Assert.Single(batches);
        Assert.Equal(["Alpha.cs", "Beta.cs", "Gamma.cs"],
            batch.Changes.Select(c => c.FilePath).Order().ToArray());
    }

    [Fact]
    public void SameFileChangedMultipleTimes_BatchContainsOnlyOneEntry()
    {
        var debounce = TimeSpan.FromMilliseconds(150);
        var time = new TestTimeProvider();
        using var sut = new FileWatcherService(debounce, time);
        var batches = new List<FileChangeBatch>();
        sut.FileChanged += b => batches.Add(b);

        sut.ScheduleDebounce(new("Repeated.cs", WatcherChangeTypes.Created, time.GetUtcNow()));
        time.Advance(TimeSpan.FromMilliseconds(10));
        sut.ScheduleDebounce(new("Repeated.cs", WatcherChangeTypes.Changed, time.GetUtcNow()));
        time.Advance(TimeSpan.FromMilliseconds(10));
        var latest = new FileChangeEvent("Repeated.cs", WatcherChangeTypes.Deleted, time.GetUtcNow());
        sut.ScheduleDebounce(latest);
        time.Advance(debounce);

        var batch = Assert.Single(batches);
        Assert.Equal(latest, Assert.Single(batch.Changes));
        Assert.Equal(latest.Timestamp, batch.Timestamp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedCallback_FromSupersededTimer_DoesNotFlushNewEvents(bool stopBeforeNewEvent)
    {
        var debounce = TimeSpan.FromMilliseconds(100);
        var time = new TestTimeProvider();
        using var sut = new FileWatcherService(debounce, time);
        var batches = new List<FileChangeBatch>();
        sut.FileChanged += batches.Add;

        sut.ScheduleDebounce(new("Old.cs", WatcherChangeTypes.Changed, time.GetUtcNow()));
        var oldTimer = Assert.Single(time.Timers);
        time.Advance(TimeSpan.FromMilliseconds(90));
        if (stopBeforeNewEvent)
        {
            sut.Stop();
        }

        sut.ScheduleDebounce(new("New.cs", WatcherChangeTypes.Changed, time.GetUtcNow()));

        oldTimer.FireQueuedCallback();
        Assert.Empty(batches);
        time.Advance(debounce - TimeSpan.FromMilliseconds(1));
        Assert.Empty(batches);
        time.Advance(TimeSpan.FromMilliseconds(1));
        var batch = Assert.Single(batches);
        Assert.Equal(stopBeforeNewEvent ? ["New.cs"] : ["New.cs", "Old.cs"],
            batch.Changes.Select(c => c.FilePath).Order().ToArray());
        time.Advance(debounce);
        _ = Assert.Single(batches);
    }

    [Fact]
    public void Changes_AfterQuietPeriod_ProduceSeparateBatches()
    {
        var debounce = TimeSpan.FromMilliseconds(100);
        var time = new TestTimeProvider();
        using var sut = new FileWatcherService(debounce, time);
        var batches = new List<FileChangeBatch>();
        sut.FileChanged += batches.Add;

        sut.ScheduleDebounce(new("Test.cs", WatcherChangeTypes.Created, time.GetUtcNow()));
        time.Advance(debounce);
        sut.ScheduleDebounce(new("Test.cs", WatcherChangeTypes.Changed, time.GetUtcNow()));
        time.Advance(debounce);

        Assert.Equal(2, batches.Count);
        Assert.Equal(WatcherChangeTypes.Created, Assert.Single(batches[0].Changes).ChangeType);
        Assert.Equal(WatcherChangeTypes.Changed, Assert.Single(batches[1].Changes).ChangeType);
    }

    [Fact]
    public async Task WatchedFileWrite_DeliversFileChange()
    {
        using var sut = new FileWatcherService(TimeSpan.FromMilliseconds(50));
        var received = new TaskCompletionSource<FileChangeBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.FileChanged += batch => received.TrySetResult(batch);
        var dir = Directory.CreateTempSubdirectory("piston-test-");
        try
        {
            sut.Start(dir.FullName);
            var filePath = Path.Combine(dir.FullName, "Test.cs");
            await File.WriteAllTextAsync(filePath, "// test", _ct);
            var batch = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
            Assert.Equal(filePath, Assert.Single(batch.Changes).FilePath);
        }
        finally
        {
            sut.Stop();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExcludedPaths_DoNotFireEvent()
    {
        var debounce = TimeSpan.FromMilliseconds(50);
        using var sut = new FileWatcherService(debounce);

        var fired = new ConcurrentQueue<FileChangeBatch>();
        sut.FileChanged += fired.Enqueue;

        var dir = Directory.CreateTempSubdirectory("piston-test-");
        try
        {
            sut.Start(dir.FullName);

            foreach (var excluded in new[] { "bin", "obj", "TestResults", ".git", ".vs", ".idea", ".piston", "node_modules" })
            {
                var excludedDir = Directory.CreateDirectory(Path.Combine(dir.FullName, excluded));
                await File.WriteAllTextAsync(Path.Combine(excludedDir.FullName, "output.cs"), "// generated", _ct);
            }

            await Task.Delay(debounce + TimeSpan.FromMilliseconds(100), _ct);

            Assert.Empty(fired);
        }
        finally
        {
            sut.Stop();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FingerprintedFilesWithAnyExtension_AreWatched()
    {
        var debounce = TimeSpan.FromMilliseconds(50);
        using var sut = new FileWatcherService(debounce);

        var fired = new ConcurrentQueue<FileChangeBatch>();
        sut.FileChanged += fired.Enqueue;

        var dir = Directory.CreateTempSubdirectory("piston-test-");
        try
        {
            sut.Start(dir.FullName);

            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "readme.txt"), "hello", _ct);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "data.json"), "{}", _ct);

            await Task.Delay(debounce + TimeSpan.FromMilliseconds(100), _ct);

            var changes = fired.SelectMany(batch => batch.Changes).Select(change => Path.GetFileName(change.FilePath)).ToArray();
            Assert.Contains("readme.txt", changes);
            Assert.Contains("data.json", changes);
        }
        finally
        {
            sut.Stop();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FingerprintedAncestorInput_IsWatched()
    {
        var debounce = TimeSpan.FromMilliseconds(50);
        using var sut = new FileWatcherService(debounce);

        var received = new TaskCompletionSource<FileChangeBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.FileChanged += batch => received.TrySetResult(batch);

        var parent = Directory.CreateTempSubdirectory("piston-test-");
        var solution = Directory.CreateDirectory(Path.Combine(parent.FullName, "solution"));
        try
        {
            sut.Start(solution.FullName);
            var inputPath = Path.Combine(parent.FullName, "global.json");
            await File.WriteAllTextAsync(inputPath, "{}", _ct);

            var batch = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
            Assert.Contains(batch.Changes, change => change.FilePath == inputPath);
        }
        finally
        {
            sut.Stop();
            parent.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MonitoringError_InvalidatesChangesAndStaysLatchedUntilRestart(bool overflow)
    {
        var time = new TestTimeProvider();
        var debounce = TimeSpan.FromMilliseconds(100);
        using var sut = new FileWatcherService(debounce, time);
        var batches = new List<FileChangeBatch>();
        sut.FileChanged += batches.Add;
        var directory = Directory.CreateTempSubdirectory("piston-test-");
        try
        {
            sut.Start(directory.FullName);
            sut.ReportMonitoringError(overflow
                ? new InternalBufferOverflowException("Source events were lost.")
                : new IOException("Monitoring stopped."));

            Assert.NotNull(sut.MonitoringError);
            Assert.Contains("Restart the daemon", sut.MonitoringError);
            Assert.True(sut.HasPendingChanges);
            time.Advance(debounce);
            Assert.Equal(directory.FullName, Assert.Single(Assert.Single(batches).Changes).FilePath);
            Assert.False(sut.HasPendingChanges);
            Assert.NotNull(sut.MonitoringError);

            sut.Start(directory.FullName);
            Assert.Null(sut.MonitoringError);
        }
        finally
        {
            sut.Stop();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DeletedAncestorInput_IsWatched()
    {
        using var sut = new FileWatcherService(TimeSpan.FromMilliseconds(50));
        var received = new TaskCompletionSource<FileChangeBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.FileChanged += batch => received.TrySetResult(batch);
        var parent = Directory.CreateTempSubdirectory("piston-test-");
        var solution = Directory.CreateDirectory(Path.Combine(parent.FullName, "solution"));
        var input = Path.Combine(parent.FullName, "global.json");
        try
        {
            await File.WriteAllTextAsync(input, "{}", _ct);
            sut.Start(solution.FullName);
            File.Delete(input);
            var batch = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
            Assert.Contains(batch.Changes, change => change.FilePath == input);
        }
        finally
        {
            sut.Stop();
            parent.Delete(recursive: true);
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private TimeSpan _elapsed;
        public List<TestTimer> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TestTimer(this, callback, state);
            _ = timer.Change(dueTime, period);
            Timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _elapsed += elapsed;
            foreach (var timer in Timers.Where(t => !t.IsDisposed && t.DueAt <= _elapsed).ToArray())
            {
                timer.FireQueuedCallback();
            }
        }

        public sealed class TestTimer(TestTimeProvider time, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan DueAt { get; private set; }
            public bool IsDisposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? TimeSpan.MaxValue : time._elapsed + dueTime;
                return !IsDisposed;
            }

            public void FireQueuedCallback()
            {
                DueAt = TimeSpan.MaxValue;
                callback(state);
            }

            public void Dispose() => IsDisposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public void HasPendingChanges_TrueOnlyWhileDebouncing()
    {
        var debounce = TimeSpan.FromMilliseconds(100);
        var time = new TestTimeProvider();
        using var sut = new FileWatcherService(debounce, time);

        Assert.False(sut.HasPendingChanges);
        sut.ScheduleDebounce(new("Test.cs", WatcherChangeTypes.Changed, time.GetUtcNow()));
        Assert.True(sut.HasPendingChanges);
        time.Advance(debounce);
        Assert.False(sut.HasPendingChanges);
    }

    [Fact]
    public void HasPendingChanges_RemainsTrueUntilFileChangedHandlerReturns()
    {
        var debounce = TimeSpan.FromMilliseconds(100);
        var time = new TestTimeProvider();
        using var sut = new FileWatcherService(debounce, time);
        bool? pendingDuringDispatch = null;
        sut.FileChanged += _ => pendingDuringDispatch = sut.HasPendingChanges;

        sut.ScheduleDebounce(new("Test.cs", WatcherChangeTypes.Changed, time.GetUtcNow()));
        time.Advance(debounce);

        Assert.True(pendingDuringDispatch);
        Assert.False(sut.HasPendingChanges);
    }

    [Fact]
    public void HasPendingChanges_ClearsWhenFileChangedHandlerThrows()
    {
        var debounce = TimeSpan.FromMilliseconds(100);
        var time = new TestTimeProvider();
        using var sut = new FileWatcherService(debounce, time);
        sut.FileChanged += _ => throw new InvalidOperationException("handler failed");

        sut.ScheduleDebounce(new("Test.cs", WatcherChangeTypes.Changed, time.GetUtcNow()));
        _ = Record.Exception(() => time.Advance(debounce));

        Assert.False(sut.HasPendingChanges);
    }
}
