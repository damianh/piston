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
    public async Task NestedDirectoriesCreatedAfterStart_AreWatched()
    {
        using var sut = new FileWatcherService(TimeSpan.FromMilliseconds(50));
        var dir = Directory.CreateTempSubdirectory("piston-test-");
        try
        {
            sut.Start(dir.FullName);
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(dir.FullName, "Project", "Sources"));
            var sourcePath = Path.Combine(sourceDirectory.FullName, "Source.cs");
            await Task.Delay(100, _ct);
            var received = new TaskCompletionSource<FileChangeBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
            sut.FileChanged += batch =>
            {
                if (batch.Changes.Any(change => change.FilePath == sourcePath))
                {
                    _ = received.TrySetResult(batch);
                }
            };

            await File.WriteAllTextAsync(sourcePath, "// source", _ct);

            var batch = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
            Assert.Contains(batch.Changes, change => change.FilePath == sourcePath);
        }
        finally
        {
            sut.Stop();
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcludedPaths_DoNotFireEvent(bool nestedProject)
    {
        var debounce = TimeSpan.FromMilliseconds(50);
        using var sut = new FileWatcherService(debounce);

        var fired = new ConcurrentQueue<FileChangeBatch>();
        sut.FileChanged += fired.Enqueue;

        var dir = Directory.CreateTempSubdirectory("piston-test-");
        try
        {
            var outputParent = nestedProject ? Directory.CreateDirectory(Path.Combine(dir.FullName, "Project")).FullName : dir.FullName;
            sut.Start(dir.FullName);

            foreach (var excluded in new[] { "bin", "obj", "TestResults", ".git", ".vs", ".idea", ".piston", "node_modules" })
            {
                var excludedDir = Directory.CreateDirectory(Path.Combine(outputParent, excluded));
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
    public async Task BuildOutputBurst_DoesNotLoseSourceMonitoring()
    {
        using var sut = new FileWatcherService(TimeSpan.FromMilliseconds(50));
        var fired = new ConcurrentQueue<FileChangeBatch>();
        sut.FileChanged += fired.Enqueue;
        var dir = Directory.CreateTempSubdirectory("piston-watcher-burst-");
        try
        {
            var projects = new[] { "First.Tests", "Second.Tests" }
                .Select(name => Directory.CreateDirectory(Path.Combine(dir.FullName, name)).FullName).ToArray();
            sut.Start(dir.FullName);
            foreach (var project in projects)
            {
                foreach (var excluded in new[] { "bin", "obj" })
                {
                    var output = Directory.CreateDirectory(Path.Combine(project, excluded, "Debug", "net10.0")).FullName;
                    for (var i = 0; i < 400; i++)
                    {
                        await File.WriteAllTextAsync(Path.Combine(output, $"Generated.Build.Output.{i:D4}.cs"), "// generated", _ct);
                    }
                }
            }

            var source = Path.Combine(projects[0], "Source.cs");
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sut.FileChanged += batch =>
            {
                if (batch.Changes.Any(change => change.FilePath == source))
                {
                    _ = received.TrySetResult();
                }
            };
            await File.WriteAllTextAsync(source, "// source", _ct);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);

            Assert.Null(sut.MonitoringError);
            Assert.All(fired.SelectMany(batch => batch.Changes), change => Assert.Equal(source, change.FilePath));
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

    [Theory]
    [InlineData("App.sln")]
    [InlineData("App.slnx")]
    [InlineData("App.slnf")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Packages.props")]
    public async Task SolutionAndBuildFiles_AreWatched(string fileName)
    {
        using var sut = new FileWatcherService(TimeSpan.FromMilliseconds(50));
        var received = new TaskCompletionSource<FileChangeBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.FileChanged += batch => received.TrySetResult(batch);
        var dir = Directory.CreateTempSubdirectory("piston-test-");
        try
        {
            sut.Start(dir.FullName);
            var filePath = Path.Combine(dir.FullName, fileName);
            await File.WriteAllTextAsync(filePath, "<Solution />", _ct);
            var batch = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
            Assert.Contains(batch.Changes, change => change.FilePath == filePath);
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

    [Fact]
    public async Task DirectoriesDeletedRightAfterCreation_DoNotFaultMonitoring()
    {
        // Tests that create and remove scratch directories once crashed the daemon (FileNotFoundException from
        // starting a watcher on a vanished directory) or latched "Access is denied" monitoring errors.
        using var sut = new FileWatcherService(TimeSpan.FromMilliseconds(50));
        var dir = Directory.CreateTempSubdirectory("piston-watcher-churn-");
        try
        {
            sut.Start(dir.FullName);
            for (var i = 0; i < 100; i++)
            {
                var scratch = Directory.CreateDirectory(Path.Combine(dir.FullName, $"scratch-{i}", "nested", "deeper"));
                await File.WriteAllTextAsync(Path.Combine(scratch.FullName, "spool.tmp"), "x", _ct);
                Directory.Delete(Path.Combine(dir.FullName, $"scratch-{i}"), recursive: true);
            }

            var source = Path.Combine(dir.FullName, "Source.cs");
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sut.FileChanged += batch =>
            {
                if (batch.Changes.Any(change => change.FilePath == source))
                {
                    _ = received.TrySetResult();
                }
            };
            await File.WriteAllTextAsync(source, "// source", _ct);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);

            Assert.Null(sut.MonitoringError);
        }
        finally
        {
            sut.Stop();
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectScope_WatchesOnlyProjectInputs(bool scopeBeforeStart)
    {
        using var sut = new FileWatcherService(TimeSpan.FromMilliseconds(50));
        var fired = new ConcurrentQueue<FileChangeBatch>();
        sut.FileChanged += fired.Enqueue;
        var dir = Directory.CreateTempSubdirectory("piston-watcher-scope-");
        try
        {
            var root = dir.FullName;
            var project = Path.Combine(root, "src", "Lib", "Lib.csproj");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            _ = Directory.CreateDirectory(Path.Combine(root, "testdata"));
            var declared = Path.Combine(root, "testdata", "input.json");
            var scope = SourceInputScope.ForProjects(Path.Combine(root, "App.slnx"), [project], [declared]);
            if (scopeBeforeStart)
            {
                sut.UpdateScope(scope);
                sut.Start(root);
            }
            else
            {
                sut.Start(root);
                sut.UpdateScope(scope);
            }

            var runtime = Directory.CreateDirectory(Path.Combine(root, ".compat-storage", "spool")).FullName;
            await File.WriteAllTextAsync(Path.Combine(runtime, "entry.tmp"), "x", _ct);
            await File.WriteAllTextAsync(Path.Combine(root, "testdata", "output.json"), "{}", _ct);
            await Task.Delay(TimeSpan.FromMilliseconds(300), _ct);
            Assert.Empty(fired);

            string[] inputs =
            [
                Path.Combine(root, "src", "Lib", "Code.cs"),
                declared,
                Path.Combine(root, "src", "Directory.Build.props"),
            ];
            foreach (var input in inputs)
            {
                var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnChanged(FileChangeBatch batch)
                {
                    if (batch.Changes.Any(change => change.FilePath == input))
                    {
                        _ = received.TrySetResult();
                    }
                }

                sut.FileChanged += OnChanged;
                await File.WriteAllTextAsync(input, "x", _ct);
                await received.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
                sut.FileChanged -= OnChanged;
            }

            Assert.All(fired.SelectMany(batch => batch.Changes), change => Assert.Contains(change.FilePath, inputs));
            Assert.Null(sut.MonitoringError);
        }
        finally
        {
            sut.Stop();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ProjectScope_IgnoresGitIgnoredProjectFilesUnlessDeclared()
    {
        using var sut = new FileWatcherService(TimeSpan.FromMilliseconds(50));
        var fired = new ConcurrentQueue<FileChangeBatch>();
        sut.FileChanged += fired.Enqueue;
        var dir = Directory.CreateTempSubdirectory("piston-watcher-ignore-");
        try
        {
            var root = dir.FullName;
            GitTestRepository.Init(root, "*.tmp", "cache/");
            var projectDir = Directory.CreateDirectory(Path.Combine(root, "Lib")).FullName;
            var declared = Path.Combine(projectDir, "Declared.tmp");
            sut.UpdateScope(SourceInputScope.ForProjects(
                Path.Combine(root, "App.slnx"), [Path.Combine(projectDir, "Lib.csproj")], [declared]));
            sut.Start(root);

            await File.WriteAllTextAsync(Path.Combine(projectDir, "spool.tmp"), "x", _ct);
            var cache = Directory.CreateDirectory(Path.Combine(projectDir, "cache")).FullName;
            await File.WriteAllTextAsync(Path.Combine(cache, "entry.bin"), "x", _ct);
            await Task.Delay(TimeSpan.FromMilliseconds(300), _ct);
            Assert.DoesNotContain(fired.SelectMany(batch => batch.Changes),
                change => change.FilePath.EndsWith("spool.tmp", StringComparison.Ordinal)
                    || change.FilePath.EndsWith("entry.bin", StringComparison.Ordinal));

            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sut.FileChanged += batch =>
            {
                if (batch.Changes.Any(change => change.FilePath == declared))
                {
                    _ = received.TrySetResult();
                }
            };
            await File.WriteAllTextAsync(declared, "x", _ct);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        }
        finally
        {
            sut.Stop();
            dir.Delete(recursive: true);
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
