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
    public async Task FilteredResumedStartupRun_IsRerunOnTheNextResume()
    {
        var state = new PistonState();
        var runner = new BlockingRunner();
        runner.BlockNext();
        await using var orchestrator = new PistonOrchestrator(
            new RaisingFileWatcher(), new StubBuildService(), runner, new RecordingImpactAnalyzer(), state);
        await orchestrator.StartAsync(Solution);
        await runner.Blocked.Task.WaitAsync(Timeout, _ct);
        orchestrator.Pause();
        _ = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Cancelled);

        state.TestFilter = "FullyQualifiedName~Some";
        orchestrator.Resume();
        var filtered = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Completed);
        Assert.Equal(TestRunTrigger.Startup, filtered.Trigger);
        Assert.False(filtered.IsFullScope);

        state.TestFilter = null;
        orchestrator.Pause();
        orchestrator.Resume();

        var run = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Completed && r.IsFullScope);
        Assert.Equal(TestRunTrigger.Startup, run.Trigger);
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

    [Fact]
    public async Task PausingDuringTests_KeepsTheTree_AndResumeContinuesWithTheUnfinishedProjects()
    {
        string[] projects = ["/fake/A.csproj", "/fake/B.csproj", "/fake/C.csproj"];
        var state = new PistonState();
        var runner = new ProjectByProjectRunner();
        var blocked = runner.BlockAt("/fake/B.csproj");
        await using var orchestrator = new PistonOrchestrator(
            new RaisingFileWatcher(), new StubBuildService(), runner, new RecordingImpactAnalyzer(projects), state);
        await orchestrator.StartAsync(Solution);
        await blocked.WaitAsync(Timeout, _ct);

        orchestrator.Pause();
        var cancelled = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Cancelled);

        Assert.Equal(TestRunTrigger.Startup, cancelled.Trigger);
        Assert.True(state.HasInterruptedRun);
        Assert.Equal(TestStatus.Passed, StatusOf(state, "A"));
        Assert.Equal(TestStatus.NotRun, StatusOf(state, "B"));
        Assert.Equal(TestStatus.NotRun, StatusOf(state, "C"));

        orchestrator.Resume();

        var run = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Completed);
        Assert.Equal(TestRunTrigger.Startup, run.Trigger);
        Assert.Equal(["/fake/B.csproj", "/fake/C.csproj"], runner.Runs[^1]);
        Assert.All(projects, p => Assert.Equal(TestStatus.Passed, StatusOf(state, p)));
        Assert.False(state.HasInterruptedRun);
    }

    [Fact]
    public async Task ChangesWhilePaused_RerunTheInterruptedRunInFull()
    {
        string[] projects = ["/fake/A.csproj", "/fake/B.csproj"];
        var state = new PistonState();
        var watcher = new RaisingFileWatcher();
        var runner = new ProjectByProjectRunner();
        var blocked = runner.BlockAt("/fake/B.csproj");
        await using var orchestrator = new PistonOrchestrator(
            watcher, new StubBuildService(), runner, new RecordingImpactAnalyzer(projects), state);
        await orchestrator.StartAsync(Solution);
        await blocked.WaitAsync(Timeout, _ct);
        orchestrator.Pause();
        _ = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Cancelled);

        watcher.Raise("/fake/A.cs");
        await Task.Delay(200, _ct);
        Assert.False(state.HasInterruptedRun);
        orchestrator.Resume();

        var run = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Completed);
        Assert.Equal(TestRunTrigger.Startup, run.Trigger);
        Assert.Equal(projects, runner.Runs[^1]);
    }

    [Fact]
    public async Task PausingAFileChangeRunDuringTests_ResumeContinuesIt_AndCoversItsChanges()
    {
        string[] projects = ["/fake/A.csproj", "/fake/B.csproj"];
        var state = new PistonState();
        var watcher = new RaisingFileWatcher();
        var analyzer = new RecordingImpactAnalyzer(projects);
        var runner = new ProjectByProjectRunner();
        await using var orchestrator = new PistonOrchestrator(
            watcher, new StubBuildService(), runner, analyzer, state);
        await orchestrator.StartAsync(Solution);
        _ = await WaitForRunAsync(state, r => r.Trigger == TestRunTrigger.Startup);

        var blocked = runner.BlockAt("/fake/B.csproj");
        watcher.Raise("/fake/A.cs");
        await blocked.WaitAsync(Timeout, _ct);
        orchestrator.Pause();
        var cancelled = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Cancelled);

        orchestrator.Resume();

        var run = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Completed && r.Sequence > cancelled.Sequence);
        Assert.Equal(TestRunTrigger.FileChange, run.Trigger);
        Assert.Equal(["/fake/B.csproj"], runner.Runs[^1]);
        Assert.Equal(0, state.PausedChangeCount);
        _ = Assert.Single(analyzer.Batches);

        // Its changes are covered: the next change is analysed alone.
        watcher.Raise("/fake/B.cs");
        _ = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Completed && r.Sequence > run.Sequence);
        Assert.Equal(["/fake/B.cs"], analyzer.Batches[^1]);
    }

    [Fact]
    public async Task PausingAResumedFileChangeRunAgain_KeepsItsChangesQueued()
    {
        string[] projects = ["/fake/A.csproj", "/fake/B.csproj"];
        var state = new PistonState();
        var watcher = new RaisingFileWatcher();
        var analyzer = new RecordingImpactAnalyzer(projects);
        var runner = new ProjectByProjectRunner();
        await using var orchestrator = new PistonOrchestrator(
            watcher, new StubBuildService(), runner, analyzer, state);
        await orchestrator.StartAsync(Solution);
        _ = await WaitForRunAsync(state, r => r.Trigger == TestRunTrigger.Startup);

        var blocked = runner.BlockAt("/fake/B.csproj");
        watcher.Raise("/fake/A.cs");
        await blocked.WaitAsync(Timeout, _ct);
        orchestrator.Pause();
        var first = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Cancelled);

        blocked = runner.BlockAt("/fake/B.csproj");
        orchestrator.Resume();
        await blocked.WaitAsync(Timeout, _ct);
        orchestrator.Pause();
        _ = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Cancelled && r.Sequence > first.Sequence);

        Assert.Equal(1, state.PausedChangeCount);

        // A changed filter rules out continuing, so resume runs for the queued change instead.
        state.TestFilter = "FullyQualifiedName~Test1";
        orchestrator.Resume();

        _ = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Completed && r.Trigger == TestRunTrigger.FileChange);
        Assert.Equal(2, analyzer.Batches.Count);
        Assert.Equal(["/fake/A.cs"], analyzer.Batches[^1]);
    }

    [Fact]
    public async Task PausingAgainBeforeThePausedRunHasStopped_StillKeepsItsResults()
    {
        string[] projects = ["/fake/A.csproj", "/fake/B.csproj", "/fake/C.csproj"];
        var state = new PistonState();
        var runner = new ProjectByProjectRunner();
        var blocked = runner.BlockAt("/fake/B.csproj");
        var hold = runner.HoldCancellation();
        await using var orchestrator = new PistonOrchestrator(
            new RaisingFileWatcher(), new StubBuildService(), runner, new RecordingImpactAnalyzer(projects), state);
        await orchestrator.StartAsync(Solution);
        await blocked.WaitAsync(Timeout, _ct);

        orchestrator.Pause();
        orchestrator.Resume();
        orchestrator.Pause();
        hold.SetResult();
        _ = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Cancelled && r.StartedAt is not null);

        Assert.Equal(TestStatus.Passed, StatusOf(state, "A"));
        Assert.True(state.HasInterruptedRun);

        orchestrator.Resume();

        _ = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Completed);
        Assert.Equal(["/fake/B.csproj", "/fake/C.csproj"], runner.Runs[^1]);
        Assert.All(projects, p => Assert.Equal(TestStatus.Passed, StatusOf(state, p)));
    }

    [Fact]
    public async Task PausingASelectedRun_WithATestFilterConfigured_ResumeContinuesIt()
    {
        string[] projects = ["/fake/A.csproj", "/fake/B.csproj", "/fake/C.csproj"];
        var state = new PistonState();
        var runner = new ProjectByProjectRunner();
        await using var orchestrator = new PistonOrchestrator(
            new RaisingFileWatcher(), new StubBuildService(), runner, new RecordingImpactAnalyzer(projects), state);
        await orchestrator.StartAsync(Solution);
        var startup = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Completed);

        state.TestFilter = "FullyQualifiedName~Test1";
        var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(Solution))!;
        var blocked = runner.BlockAt("/fake/B.csproj");
        _ = orchestrator.RunSelectedAsync(new TestSelection([.. projects.Select(p => TestProjectNames.ProjectId(solutionDirectory, p))]));
        await blocked.WaitAsync(Timeout, _ct);
        orchestrator.Pause();
        var cancelled = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Cancelled && r.Sequence > startup.Sequence);

        orchestrator.Resume();

        var run = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Completed && r.Sequence > cancelled.Sequence);
        Assert.Equal(TestRunTrigger.Targeted, run.Trigger);
        Assert.Equal(["/fake/B.csproj", "/fake/C.csproj"], runner.Runs[^1]);
    }

    [Fact]
    public async Task PausingAfterAProjectReportedNoTests_ResumeRunsItAgain()
    {
        string[] projects = ["/fake/A.csproj", "/fake/B.csproj"];
        var state = new PistonState();
        var runner = new ProjectByProjectRunner();
        _ = runner.Empty.TryAdd("/fake/A.csproj", 0);
        var blocked = runner.BlockAt("/fake/B.csproj");
        await using var orchestrator = new PistonOrchestrator(
            new RaisingFileWatcher(), new StubBuildService(), runner, new RecordingImpactAnalyzer(projects), state);
        await orchestrator.StartAsync(Solution);
        await blocked.WaitAsync(Timeout, _ct);
        orchestrator.Pause();
        var cancelled = await WaitForRunAsync(state, r => r.Status == TestRunStatus.Cancelled);

        orchestrator.Resume();

        _ = await WaitForRunAsync(state, r => r.Status != TestRunStatus.Cancelled && r.Sequence > cancelled.Sequence);
        Assert.Equal(projects, runner.Runs[^1]);
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

    private static TestStatus StatusOf(PistonState state, string project) =>
        Assert.Single(Assert.Single(state.TestSuites, s => s.Name == Path.GetFileNameWithoutExtension(project)).Tests).Status;

    // Runs projects one by one, reporting each on completion; can block a project until the run is cancelled.
    private sealed class ProjectByProjectRunner : ITestRunnerService
    {
        private readonly ConcurrentQueue<IReadOnlyList<string>> _runs = new();
        private string? _blockAt;
        private TaskCompletionSource? _cancellationHold;

        public IReadOnlyList<IReadOnlyList<string>> Runs => [.. _runs];

        // Projects that report no tests when run.
        public ConcurrentDictionary<string, byte> Empty { get; } = new();

        // Keeps a blocked run from finishing after it is cancelled until the returned source completes.
        public TaskCompletionSource HoldCancellation()
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _cancellationHold, hold);
            return hold;
        }

        public TaskCompletionSource Blocked { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Blocks the next run when it reaches the project; the returned task completes once it is blocked.
        public Task BlockAt(string project)
        {
            Blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _blockAt, project);
            return Blocked.Task;
        }

        public Task DiscoverTestsAsync(
            IReadOnlyList<string> testProjectPaths, string? filter, Action<string, IReadOnlyList<TestResult>> onDiscovered,
            CancellationToken ct)
        {
            foreach (var project in testProjectPaths)
            {
                onDiscovered(project, [Test(project, TestStatus.NotRun)]);
            }

            return Task.CompletedTask;
        }

        public Task<TestRunResult> RunTestsAsync(
            string solutionPath, string? filter, Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            throw new NotSupportedException();

        public async Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            Action<IReadOnlyList<TestSuite>>? onProgress, Action<ProjectTestResult>? onProjectCompleted,
            CancellationToken ct)
        {
            var projects = testProjectPaths ?? [];
            _runs.Enqueue(projects);
            var suites = new List<TestSuite>();
            var results = new List<ProjectTestResult>();
            foreach (var project in projects)
            {
                if (Interlocked.CompareExchange(ref _blockAt, null, project) == project)
                {
                    _ = Blocked.TrySetResult();
                    try
                    {
                        await Task.Delay(System.Threading.Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException) when (Volatile.Read(ref _cancellationHold) is { } hold)
                    {
                        await hold.Task;
                        throw;
                    }
                }

                var suite = new TestSuite(Path.GetFileNameWithoutExtension(project),
                    Empty.ContainsKey(project) ? [] : [Test(project, TestStatus.Passed)],
                    DateTimeOffset.UtcNow, TimeSpan.Zero);
                suites.Add(suite);
                var result = new ProjectTestResult(project, [suite], null, [], false);
                results.Add(result);
                onProjectCompleted?.Invoke(result);
            }

            return new TestRunResult(suites, null, []) { Projects = results };
        }

        private static TestResult Test(string project, TestStatus status) =>
            new(Path.GetFileNameWithoutExtension(project) + ".Test1", "Test1", status, TimeSpan.Zero, null, null, null, null);
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

    private sealed class RecordingImpactAnalyzer(string[]? projects = null) : IImpactAnalyzer
    {
        private readonly string[] _projects = projects ?? Projects;
        private readonly ConcurrentQueue<IReadOnlyList<string>> _batches = new();

        public IReadOnlyList<IReadOnlyList<string>> Batches => [.. _batches];

        public Task InitializeAsync(string solutionPath, CancellationToken ct) => Task.CompletedTask;

        public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes)
        {
            _batches.Enqueue([.. changes.Select(c => c.FilePath)]);
            return AnalyzeFullRun();
        }

        public ImpactAnalysisResult AnalyzeFullRun() => new(_projects, _projects, RequiresGraphRebuild: false, IsFullRun: true);
        public void InvalidateGraph() { }
        public IReadOnlyList<string> GetAllTestProjectPaths() => _projects;
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
