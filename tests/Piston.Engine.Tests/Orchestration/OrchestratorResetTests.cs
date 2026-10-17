// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class OrchestratorResetTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private const string Solution = "/fake/solution.slnx";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string[] Projects = ["a.csproj"];

    [Fact]
    public async Task Reset_ClearsResults_ReloadsGraph_RestartsWatcher_AndRunsAllTests()
    {
        var state = new PistonState();
        var watcher = new CountingFileWatcher();
        var analyzer = new CountingImpactAnalyzer();
        await using var orchestrator = new PistonOrchestrator(
            watcher, new StubBuildService(), new StubTestRunnerService(Projects, TimeSpan.Zero), analyzer, state);
        await orchestrator.StartAsync(Solution);
        _ = await WaitForRunAsync(state, TestRunTrigger.Startup);
        _ = Assert.Single(state.TestSuites);

        var clearedDuringReset = false;
        state.StateChanged += () => clearedDuringReset |= state.TestSuites.Count == 0;
        await orchestrator.ResetAsync().WaitAsync(Timeout, _ct);

        var run = await WaitForRunAsync(state, TestRunTrigger.Forced);
        Assert.True(clearedDuringReset);
        Assert.Equal(TestRunStatus.Completed, run.Status);
        _ = Assert.Single(state.TestSuites);
        Assert.Equal(2, analyzer.InitializeCount);
        Assert.Equal(1, analyzer.InvalidateCount);
        Assert.Equal(2, watcher.StartCount);
        Assert.Contains(state.RecentRuns, r => r.Trigger == TestRunTrigger.Startup);
    }

    [Fact]
    public async Task Reset_CancelsActiveRun_AndStartsAFreshRun()
    {
        var state = new PistonState();
        var runner = new FirstRunBlocksUntilCancelledRunner();
        await using var orchestrator = new PistonOrchestrator(
            new CountingFileWatcher(), new StubBuildService(), runner, new CountingImpactAnalyzer(), state);
        await orchestrator.StartAsync(Solution);
        await runner.FirstRunStarted.Task.WaitAsync(Timeout, _ct);

        await orchestrator.ResetAsync().WaitAsync(Timeout, _ct);

        var run = await WaitForRunAsync(state, TestRunTrigger.Forced);
        Assert.Equal(TestRunStatus.Completed, run.Status);
        Assert.True(runner.FirstRunCancelled);
        _ = Assert.Single(state.TestSuites);
    }

    [Fact]
    public async Task Reset_AfterStop_RearmsTheEngine()
    {
        var state = new PistonState();
        await using var orchestrator = new PistonOrchestrator(
            new CountingFileWatcher(), new StubBuildService(), new StubTestRunnerService(Projects, TimeSpan.Zero),
            new CountingImpactAnalyzer(), state);
        await orchestrator.StartAsync(Solution);
        _ = await WaitForRunAsync(state, TestRunTrigger.Startup);
        orchestrator.Stop();

        await orchestrator.ResetAsync().WaitAsync(Timeout, _ct);

        var run = await WaitForRunAsync(state, TestRunTrigger.Forced);
        Assert.Equal(TestRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task Reset_BeforeStart_Throws()
    {
        await using var orchestrator = new PistonOrchestrator(
            new CountingFileWatcher(), new StubBuildService(), new StubTestRunnerService(Projects, TimeSpan.Zero),
            new CountingImpactAnalyzer(), new PistonState());

        _ = await Assert.ThrowsAsync<InvalidOperationException>(orchestrator.ResetAsync);
    }

    private static async Task<TestRunRecord> WaitForRunAsync(PistonState state, TestRunTrigger trigger)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (state.LastRun is not { Status: TestRunStatus.Completed } run || run.Trigger != trigger)
        {
            await Task.Delay(10, timeout.Token);
        }
        return state.LastRun;
    }

    private sealed class CountingFileWatcher : IFileWatcherService
    {
        private int _startCount;

        public event Action<FileChangeBatch>? FileChanged { add { } remove { } }

        public int StartCount => Volatile.Read(ref _startCount);

        public void Start(string directory) => _ = Interlocked.Increment(ref _startCount);
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class CountingImpactAnalyzer : IImpactAnalyzer
    {
        private int _initializeCount;
        private int _invalidateCount;

        public int InitializeCount => Volatile.Read(ref _initializeCount);
        public int InvalidateCount => Volatile.Read(ref _invalidateCount);

        public Task InitializeAsync(string solutionPath, CancellationToken ct)
        {
            _ = Interlocked.Increment(ref _initializeCount);
            return Task.CompletedTask;
        }

        public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes) => AnalyzeFullRun();
        public ImpactAnalysisResult AnalyzeFullRun() => new(Projects, Projects, RequiresGraphRebuild: false, IsFullRun: true);
        public void InvalidateGraph() => _ = Interlocked.Increment(ref _invalidateCount);
        public IReadOnlyList<string> GetAllTestProjectPaths() => Projects;
        public bool IsMtpProject(string projectPath) => false;
        public string? GetMtpOutputPath(string projectPath) => null;
    }

    private sealed class FirstRunBlocksUntilCancelledRunner : ITestRunnerService
    {
        private readonly StubTestRunnerService _inner = new(Projects, TimeSpan.Zero);
        private int _calls;

        public TaskCompletionSource FirstRunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FirstRunCancelled { get; private set; }

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
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _ = FirstRunStarted.TrySetResult();
                try
                {
                    await Task.Delay(System.Threading.Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    FirstRunCancelled = true;
                    throw;
                }
            }

            return await _inner.RunTestsAsync(
                solutionPath, testProjectPaths, filter, collectCoverage, onProgress, onProjectCompleted, ct);
        }
    }
}
