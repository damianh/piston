// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class OrchestratorPauseTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private const string Solution = "/fake/solution.slnx";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string[] Projects = ["a.csproj"];

    [Fact]
    public async Task FileChangesWhilePaused_AreQueued_AndResumeRunsOnceForAllOfThem()
    {
        var state = new PistonState();
        var watcher = new RaisingFileWatcher();
        var analyzer = new RecordingImpactAnalyzer();
        await using var orchestrator = new PistonOrchestrator(
            watcher, new StubBuildService(), new BlockingRunner(), analyzer, state);
        await orchestrator.StartAsync(Solution);
        _ = await WaitForRunAsync(state, r => r.Trigger == TestRunTrigger.Startup);

        orchestrator.Pause();
        watcher.Raise("/fake/A.cs");
        watcher.Raise("/fake/B.cs");
        watcher.Raise("/fake/A.cs");
        await Task.Delay(200, _ct);

        Assert.True(state.IsPaused);
        Assert.Equal(2, state.PausedChangeCount);
        Assert.DoesNotContain(state.RecentRuns, r => r.Trigger == TestRunTrigger.FileChange);
        Assert.Empty(analyzer.Batches);

        orchestrator.Resume();

        var run = await WaitForRunAsync(state, r => r.Trigger == TestRunTrigger.FileChange);
        Assert.Equal(TestRunStatus.Completed, run.Status);
        Assert.False(state.IsPaused);
        Assert.Equal(0, state.PausedChangeCount);
        var batch = Assert.Single(analyzer.Batches);
        Assert.Equal(["/fake/A.cs", "/fake/B.cs"], batch.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Pause_CancelsTheActiveRun_AndQueuesItsChanges()
    {
        var state = new PistonState();
        var watcher = new RaisingFileWatcher();
        var analyzer = new RecordingImpactAnalyzer();
        var runner = new BlockingRunner();
        await using var orchestrator = new PistonOrchestrator(
            watcher, new StubBuildService(), runner, analyzer, state);
        await orchestrator.StartAsync(Solution);
        _ = await WaitForRunAsync(state, r => r.Trigger == TestRunTrigger.Startup);

        runner.BlockNext();
        watcher.Raise("/fake/A.cs");
        await runner.Blocked.Task.WaitAsync(Timeout, _ct);
        orchestrator.Pause();

        var cancelled = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Cancelled);
        Assert.Equal(TestRunTrigger.FileChange, cancelled.Trigger);
        Assert.Contains("paused", cancelled.StatusReason, StringComparison.Ordinal);
        Assert.Equal(1, state.PausedChangeCount);

        orchestrator.Resume();

        var run = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Completed && r.Sequence > cancelled.Sequence);
        Assert.Equal(TestRunTrigger.FileChange, run.Trigger);
        Assert.Equal(["/fake/A.cs"], analyzer.Batches[^1]);
    }

    [Fact]
    public async Task RequestedRunWhilePaused_Runs_AndCoversTheQueuedChanges()
    {
        var state = new PistonState();
        var watcher = new RaisingFileWatcher();
        await using var orchestrator = new PistonOrchestrator(
            watcher, new StubBuildService(), new BlockingRunner(), new RecordingImpactAnalyzer(), state);
        await orchestrator.StartAsync(Solution);
        _ = await WaitForRunAsync(state, r => r.Trigger == TestRunTrigger.Startup);

        orchestrator.Pause();
        watcher.Raise("/fake/A.cs");
        var run = await orchestrator.RequestRunAsync().WaitAsync(Timeout, _ct);

        Assert.Equal(TestRunStatus.Completed, run!.Status);
        Assert.True(state.IsPaused);
        Assert.Equal(0, state.PausedChangeCount);

        var runs = state.RecentRuns.Count;
        orchestrator.Resume();
        await Task.Delay(200, _ct);
        Assert.Equal(runs, state.RecentRuns.Count);
    }

    [Fact]
    public async Task PausingTheStartupRun_RerunsItOnResume()
    {
        var state = new PistonState();
        var runner = new BlockingRunner();
        runner.BlockNext();
        await using var orchestrator = new PistonOrchestrator(
            new RaisingFileWatcher(), new StubBuildService(), runner, new RecordingImpactAnalyzer(), state);
        await orchestrator.StartAsync(Solution);
        await runner.Blocked.Task.WaitAsync(Timeout, _ct);

        orchestrator.Pause();
        var cancelled = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Cancelled);
        Assert.Equal(TestRunTrigger.Startup, cancelled.Trigger);

        orchestrator.Resume();

        var run = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Completed);
        Assert.Equal(TestRunTrigger.Startup, run.Trigger);
        _ = Assert.Single(state.TestSuites);
    }

    [Fact]
    public async Task FilteredRequestedRunWhilePaused_KeepsTheQueuedChanges()
    {
        var state = new PistonState();
        var watcher = new RaisingFileWatcher();
        await using var orchestrator = new PistonOrchestrator(
            watcher, new StubBuildService(), new BlockingRunner(), new RecordingImpactAnalyzer(), state);
        await orchestrator.StartAsync(Solution);
        _ = await WaitForRunAsync(state, r => r.Trigger == TestRunTrigger.Startup);

        orchestrator.Pause();
        watcher.Raise("/fake/A.cs");
        state.TestFilter = "FullyQualifiedName~Some";
        var run = await orchestrator.RequestRunAsync().WaitAsync(Timeout, _ct);

        Assert.Equal(TestRunStatus.Completed, run!.Status);
        Assert.False(run.IsFullScope);
        Assert.Equal(1, state.PausedChangeCount);
    }

    [Fact]
    public async Task FilteredResetWhilePaused_KeepsTheQueuedChanges()
    {
        var state = new PistonState();
        var watcher = new RaisingFileWatcher();
        await using var orchestrator = new PistonOrchestrator(
            watcher, new StubBuildService(), new BlockingRunner(), new RecordingImpactAnalyzer(), state);
        await orchestrator.StartAsync(Solution);
        _ = await WaitForRunAsync(state, r => r.Trigger == TestRunTrigger.Startup);

        orchestrator.Pause();
        watcher.Raise("/fake/A.cs");
        state.TestFilter = "FullyQualifiedName~Some";
        await orchestrator.ResetAsync().WaitAsync(Timeout, _ct);

        var run = await WaitForRunAsync(state, r => r.Trigger == TestRunTrigger.Forced && r.Status == TestRunStatus.Completed);
        Assert.False(run.IsFullScope);
        Assert.Equal(1, state.PausedChangeCount);
    }

    [Fact]
    public async Task PausingAFileChangeRun_ThatSupersededTheResumedStartupRun_RerunsStartupOnResume()
    {
        var state = new PistonState();
        var watcher = new RaisingFileWatcher();
        var runner = new BlockingRunner();
        runner.BlockNext();
        await using var orchestrator = new PistonOrchestrator(
            watcher, new StubBuildService(), runner, new RecordingImpactAnalyzer(), state);
        await orchestrator.StartAsync(Solution);
        await runner.Blocked.Task.WaitAsync(Timeout, _ct);
        orchestrator.Pause();
        _ = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Cancelled);

        var blocked = runner.BlockNextRun();
        orchestrator.Resume();
        await blocked.WaitAsync(Timeout, _ct);

        blocked = runner.BlockNextRun();
        watcher.Raise("/fake/A.cs");
        await blocked.WaitAsync(Timeout, _ct);
        orchestrator.Pause();
        _ = await WaitForRunAsync(state, r => r.Trigger == TestRunTrigger.FileChange && r.Status == TestRunStatus.Cancelled);

        orchestrator.Resume();

        var run = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Completed);
        Assert.Equal(TestRunTrigger.Startup, run.Trigger);
    }

    private static async Task<TestRunRecord> WaitForRunAsync(PistonState state, Func<TestRunRecord, bool> match)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        TestRunRecord? found;
        while ((found = state.RecentRuns.FirstOrDefault(match)) is null)
        {
            await Task.Delay(10, timeout.Token);
        }

        return found;
    }

    private sealed class RaisingFileWatcher : IFileWatcherService
    {
        public event Action<FileChangeBatch>? FileChanged;

        public void Raise(string path)
        {
            var now = DateTimeOffset.UtcNow;
            FileChanged?.Invoke(new FileChangeBatch([new FileChangeEvent(path, WatcherChangeTypes.Changed, now)], now));
        }

        public void Start(string directory) { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class RecordingImpactAnalyzer : IImpactAnalyzer
    {
        private readonly ConcurrentQueue<IReadOnlyList<string>> _batches = new();

        public IReadOnlyList<IReadOnlyList<string>> Batches => [.. _batches];

        public Task InitializeAsync(string solutionPath, CancellationToken ct) => Task.CompletedTask;

        public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes)
        {
            _batches.Enqueue([.. changes.Select(c => c.FilePath)]);
            return AnalyzeFullRun();
        }

        public ImpactAnalysisResult AnalyzeFullRun() => new(Projects, Projects, RequiresGraphRebuild: false, IsFullRun: true);
        public void InvalidateGraph() { }
        public IReadOnlyList<string> GetAllTestProjectPaths() => Projects;
        public bool IsMtpProject(string projectPath) => false;
        public string? GetMtpOutputPath(string projectPath) => null;
    }

    private sealed class BlockingRunner : ITestRunnerService
    {
        private readonly StubTestRunnerService _inner = new(Projects, TimeSpan.Zero);
        private int _blockNext;
        private TaskCompletionSource _nextBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BlockNext() => Volatile.Write(ref _blockNext, 1);

        // Blocks the next test execution until cancelled; the returned task completes once it is blocked.
        public Task BlockNextRun()
        {
            _nextBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            BlockNext();
            return _nextBlocked.Task;
        }

        public Task<TestRunResult> RunTestsAsync(
            string solutionPath, string? filter, Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            RunTestsAsync(solutionPath, null, filter, false, onProgress, null, ct);

        public Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            RunTestsAsync(solutionPath, testProjectPaths, filter, collectCoverage, onProgress, null, ct);

        public async Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            Action<IReadOnlyList<TestSuite>>? onProgress, Action<ProjectTestResult>? onProjectCompleted,
            CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _blockNext, 0) == 1)
            {
                _ = Blocked.TrySetResult();
                _ = _nextBlocked.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, ct);
            }

            return await _inner.RunTestsAsync(
                solutionPath, testProjectPaths, filter, collectCoverage, onProgress, onProjectCompleted, ct);
        }
    }
}
