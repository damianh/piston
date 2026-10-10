using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class OrchestratorSelectiveBuildTests
{
    private const string Lib = "/repo/Lib/Lib.csproj";
    private const string LibTests = "/repo/Lib.Tests/Lib.Tests.csproj";

    [Fact]
    public void BuildTargetPaths_IncludesAffectedTestProjects()
    {
        var result = new ImpactAnalysisResult([Lib], [LibTests], RequiresGraphRebuild: false, IsFullRun: false);

        Assert.Equal([Lib, LibTests], result.BuildTargetPaths);
    }

    [Fact]
    public void BuildTargetPaths_TestOnlyChange_IsTheTestProject()
    {
        var result = new ImpactAnalysisResult([], [LibTests], RequiresGraphRebuild: false, IsFullRun: false);

        Assert.Equal([LibTests], result.BuildTargetPaths);
    }

    [Fact]
    public void BuildTargetPaths_FullRun_IsEmpty()
    {
        var result = new ImpactAnalysisResult([], [], RequiresGraphRebuild: false, IsFullRun: true);

        Assert.Empty(result.BuildTargetPaths);
    }

    [Fact]
    public void MergeTestSuites_ReplacesRerunTestsByFqn_EvenWhenSuiteNamesDiffer()
    {
        static TestResult T(string fqn, TestStatus status) =>
            new(fqn, fqn, status, TimeSpan.Zero, null, null, null, null);

        var existing = new[]
        {
            new TestSuite("@machine 10:00:00", [T("Lib.T.A", TestStatus.Passed), T("Lib.T.B", TestStatus.Passed)], DateTimeOffset.UtcNow, TimeSpan.Zero),
            new TestSuite("@machine 10:00:01", [T("Other.T.C", TestStatus.Passed)], DateTimeOffset.UtcNow, TimeSpan.Zero),
        };
        var rerun = new[]
        {
            new TestSuite("@machine 10:05:00", [T("Lib.T.A", TestStatus.Failed)], DateTimeOffset.UtcNow, TimeSpan.Zero),
        };

        var merged = PistonOrchestrator.MergeTestSuites(existing, rerun).SelectMany(s => s.Tests).ToList();

        Assert.Equal(3, merged.Count);
        Assert.Equal(TestStatus.Failed, Assert.Single(merged, t => t.FullyQualifiedName == "Lib.T.A").Status);
        Assert.Contains(merged, t => t.FullyQualifiedName == "Lib.T.B" && t.Status == TestStatus.Passed);
        Assert.Contains(merged, t => t.FullyQualifiedName == "Other.T.C" && t.Status == TestStatus.Passed);
    }

    [Theory]
    [InlineData(new[] { Lib }, new[] { LibTests }, new[] { Lib, LibTests })]
    [InlineData(new string[0], new[] { LibTests }, new[] { LibTests })]
    public async Task SelectiveRun_BuildsAffectedTestProjects_BeforeRunningThem(
        string[] affected, string[] affectedTests, string[] expectedBuildTargets)
    {
        var state = new PistonState();
        var watcher = new StubFileWatcherService();
        var build = new RecordingBuildService();
        var runner = new RecordingTestRunner();
        var analyzer = new FixedImpactAnalyzer(
            new ImpactAnalysisResult(affected, affectedTests, RequiresGraphRebuild: false, IsFullRun: false));

        using var orchestrator = new PistonOrchestrator(watcher, build, runner, analyzer, state);
        await orchestrator.StartAsync("/repo/All.slnx");
        await runner.WaitForCallsAsync(1);

        watcher.TriggerChange(new FileChangeBatch(
            [new FileChangeEvent("/repo/x.cs", WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow));
        await runner.WaitForCallsAsync(2);

        Assert.Null(build.Calls[0]);
        Assert.Equal(expectedBuildTargets, build.Calls[1]);
        Assert.Equal(affectedTests, runner.Calls[1]);
    }

    [Fact]
    public async Task SelectiveRun_WithNoAffectedTestProjects_BuildsButSkipsTests()
    {
        var state = new PistonState();
        var watcher = new StubFileWatcherService();
        var build = new RecordingBuildService();
        var runner = new RecordingTestRunner();
        var analyzer = new FixedImpactAnalyzer(
            new ImpactAnalysisResult([Lib], [], RequiresGraphRebuild: false, IsFullRun: false));

        using var orchestrator = new PistonOrchestrator(watcher, build, runner, analyzer, state);
        await orchestrator.StartAsync("/repo/All.slnx");
        await runner.WaitForCallsAsync(1);

        watcher.TriggerChange(new FileChangeBatch(
            [new FileChangeEvent("/repo/Lib/x.cs", WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow));
        await build.WaitForCallsAsync(2);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (state.Phase != PistonPhase.Watching && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(PistonPhase.Watching, state.Phase);
        Assert.Equal([Lib], build.Calls[1]);
        Assert.Single(runner.Calls); // only the initial full run executed tests
    }

    private sealed class FixedImpactAnalyzer(ImpactAnalysisResult selective) : IImpactAnalyzer
    {
        public Task InitializeAsync(string solutionPath, CancellationToken ct) => Task.CompletedTask;
        public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes) => selective;
        public ImpactAnalysisResult AnalyzeFullRun() => new([], [], RequiresGraphRebuild: false, IsFullRun: true);
        public void InvalidateGraph() { }
        public IReadOnlyList<string> GetAllTestProjectPaths() => [LibTests];
        public bool IsMtpProject(string projectPath) => false;
        public string? GetMtpOutputPath(string projectPath) => null;
    }

    private sealed class RecordingBuildService : IBuildService
    {
        private readonly CallCounter _counter = new();
        public List<IReadOnlyList<string>?> Calls { get; } = [];

        public Task WaitForCallsAsync(int count) => _counter.WaitForAsync(count);

        public Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct) =>
            BuildAsync(solutionPath, null, ct);

        public Task<BuildResult> BuildAsync(string solutionPath, IReadOnlyList<string>? projectPaths, CancellationToken ct)
        {
            lock (Calls) Calls.Add(projectPaths?.ToList());
            _counter.Increment();
            return Task.FromResult(new BuildResult(BuildStatus.Succeeded, [], [], TimeSpan.Zero));
        }
    }

    private sealed class RecordingTestRunner : ITestRunnerService
    {
        private readonly CallCounter _counter = new();
        public List<IReadOnlyList<string>?> Calls { get; } = [];

        public Task WaitForCallsAsync(int count) => _counter.WaitForAsync(count);

        public Task<TestRunResult> RunTestsAsync(string solutionPath, string? filter,
            Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            RunTestsAsync(solutionPath, null, filter, false, onProgress, null, ct);

        public Task<TestRunResult> RunTestsAsync(string solutionPath, IReadOnlyList<string>? testProjectPaths,
            string? filter, bool collectCoverage, Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            RunTestsAsync(solutionPath, testProjectPaths, filter, collectCoverage, onProgress, null, ct);

        public Task<TestRunResult> RunTestsAsync(string solutionPath, IReadOnlyList<string>? testProjectPaths,
            string? filter, bool collectCoverage, Action<IReadOnlyList<TestSuite>>? onProgress,
            Action<ProjectTestResult>? onProjectCompleted, CancellationToken ct)
        {
            lock (Calls) Calls.Add(testProjectPaths?.ToList());
            _counter.Increment();
            return Task.FromResult(new TestRunResult([], null, []));
        }
    }

    private sealed class CallCounter
    {
        private int _count;

        public void Increment() => Interlocked.Increment(ref _count);

        public async Task WaitForAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref _count) < count)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Expected {count} call(s), saw {_count}.");
                await Task.Delay(10);
            }

            // Let the orchestrator finish the current pipeline before the next trigger.
            await Task.Delay(50);
        }
    }
}
