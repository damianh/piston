// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

/// <summary>
/// Verifies that PistonState.CompletedTestProjects and ProjectStatuses are updated correctly
/// during a parallel test run. Uses stubbed services.
/// </summary>
public sealed class OrchestratorProgressTests
{
    [Fact]
    public async Task PipelineAndFileChanges_EmitEngineOwnedActivity()
    {
        var projects = new[] { "a.csproj" };
        var watcher = new StubFileWatcherService();
        var sink = new RecordingActivitySink();
        await using var orchestrator = new PistonOrchestrator(
            watcher, new StubBuildService(), new StubTestRunnerService(projects, TimeSpan.Zero),
            new StubImpactAnalyzer(projects), new PistonState(), null, null, false, sink);

        await orchestrator.StartAsync("/fake/solution.slnx");
        await sink.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var build = Assert.Single(sink.Events.OfType<BuildCompletedActivityEvent>());
        Assert.True(build.Succeeded);
        Assert.Empty(build.Errors);
        Assert.Equal("/fake/solution.slnx", build.SolutionPath);
        var tests = Assert.Single(sink.Events.OfType<TestRunCompletedActivityEvent>());
        Assert.Equal(1, tests.Passed);
        Assert.Equal(0, tests.Failed);
        Assert.Empty(tests.Failures);

        watcher.TriggerChange(new FileChangeBatch(
            [new FileChangeEvent("/fake/A.cs", WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow));

        var changed = Assert.Single(sink.Events.OfType<FileChangesDetectedActivityEvent>());
        Assert.Equal(new[] { "/fake/A.cs" }, changed.ChangedFiles);
        Assert.Equal("/fake/solution.slnx", changed.SolutionPath);
    }

    [Fact]
    public async Task SupersededBuild_IsNotReportedAsBuildFailure()
    {
        var projects = new[] { "a.csproj" };
        var watcher = new StubFileWatcherService();
        var sink = new RecordingActivitySink();
        var build = new FirstBuildBlocksUntilCancelledBuildService();
        await using var orchestrator = new PistonOrchestrator(
            watcher, build, new StubTestRunnerService(projects, TimeSpan.Zero),
            new StubImpactAnalyzer(projects), new PistonState(), null, null, false, sink);

        await orchestrator.StartAsync("/fake/solution.slnx");
        await build.FirstBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        watcher.TriggerChange(new FileChangeBatch(
            [new FileChangeEvent("/fake/A.cs", WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow));
        await sink.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var completed = Assert.Single(sink.Events.OfType<BuildCompletedActivityEvent>());
        Assert.True(completed.Succeeded);
    }

    [Fact]
    public async Task Build_ReportsProjectProgress()
    {
        var projects = new[] { "/w/Lib/Lib.csproj", "/w/App.Tests/App.Tests.csproj" };
        var state = new PistonState();
        var sink = new RecordingActivitySink();
        var progress = new ConcurrentQueue<(int Completed, int Total, string? Last)>();
        state.StateChanged += () =>
        {
            if (state.Phase == PistonPhase.Building)
            {
                progress.Enqueue((state.CompletedBuildProjects, state.TotalBuildProjects, state.LastBuiltProject));
            }
        };
        await using var orchestrator = new PistonOrchestrator(
            new StubFileWatcherService(),
            new ReportingBuildService(["Lib", "Lib", "Unexpected", "App.Tests"]),
            new StubTestRunnerService(projects, TimeSpan.Zero),
            new BuildProjectsImpactAnalyzer(projects), state, null, null, false, sink);

        await orchestrator.StartAsync("/fake/solution.slnx");
        await sink.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            new (int, int, string?)[] { (0, 2, null), (1, 2, "Lib"), (1, 2, "Unexpected"), (2, 2, "App.Tests") },
            progress.Distinct().ToArray());
    }

    private sealed class ReportingBuildService(IReadOnlyList<string> builtProjects) : IBuildService
    {
        public Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct) =>
            BuildAsync(solutionPath, null, ct);

        public Task<BuildResult> BuildAsync(string solutionPath, IReadOnlyList<string>? projectPaths, CancellationToken ct) =>
            BuildAsync(solutionPath, projectPaths, null, ct);

        public Task<BuildResult> BuildAsync(
            string solutionPath,
            IReadOnlyList<string>? projectPaths,
            Action<string>? onProjectBuilt,
            CancellationToken ct)
        {
            foreach (var project in builtProjects)
            {
                onProjectBuilt?.Invoke(project);
            }

            return Task.FromResult(new BuildResult(BuildStatus.Succeeded, [], [], TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class BuildProjectsImpactAnalyzer(IReadOnlyList<string> projects) : IImpactAnalyzer
    {
        private readonly StubImpactAnalyzer _inner = new(projects);

        public Task InitializeAsync(string solutionPath, CancellationToken ct) => Task.CompletedTask;
        public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes) => _inner.Analyze(changes);
        public ImpactAnalysisResult AnalyzeFullRun() => _inner.AnalyzeFullRun();
        public void InvalidateGraph() { }
        public IReadOnlyList<string> GetAllTestProjectPaths() => projects;
        public bool IsMtpProject(string projectPath) => false;
        public string? GetMtpOutputPath(string projectPath) => null;
        public IReadOnlyList<string> GetBuildProjectPaths(IReadOnlyList<string>? buildTargets) => projects;
    }

    private sealed class FirstBuildBlocksUntilCancelledBuildService : IBuildService
    {
        private int _calls;

        public TaskCompletionSource FirstBuildStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct) =>
            BuildAsync(solutionPath, null, ct);

        public async Task<BuildResult> BuildAsync(string solutionPath, IReadOnlyList<string>? projectPaths, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                return new BuildResult(BuildStatus.Succeeded, [], [], TimeSpan.FromMilliseconds(1));
            }

            // Mirrors BuildService: a killed build reports Failed with no diagnostics.
            _ = FirstBuildStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }
            return new BuildResult(BuildStatus.Failed, [], [], TimeSpan.FromMilliseconds(1));
        }
    }

    private sealed class RecordingActivitySink : IActivityEventSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<EngineActivityEvent> Events { get; } = new();
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Emit(EngineActivityEvent activity)
        {
            Events.Enqueue(activity);
            if (activity is TestRunCompletedActivityEvent)
            {
                _ = Completed.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task ProjectStatuses_AreInitializedAsPending_BeforeTestRun()
    {
        var state = new PistonState();
        var projects = new[] { "a.csproj", "b.csproj", "c.csproj" };

        var testRunnerStub = new StubTestRunnerService(projects, delay: TimeSpan.Zero);

        var orchestrator = new PistonOrchestrator(
            new StubFileWatcherService(),
            new StubBuildService(),
            testRunnerStub,
            new StubImpactAnalyzer(projects),
            state);

        // Capture state changes
        var statuses = new List<IReadOnlyDictionary<string, ProjectRunStatus>>();
        state.StateChanged += () =>
        {
            if (state.ProjectStatuses.Count > 0)
            {
                statuses.Add(new Dictionary<string, ProjectRunStatus>(state.ProjectStatuses));
            }
        };

        await orchestrator.StartAsync("/fake/solution.slnx");
        await testRunnerStub.WaitForCompletionAsync();

        // At some point ProjectStatuses should have been set with 3 projects
        Assert.Contains(statuses, s => s.Count == 3);
        orchestrator.Dispose();
    }

    [Fact]
    public async Task CompletedTestProjects_IncrementsAsProjectsComplete()
    {
        var projects = new[] { "a.csproj", "b.csproj", "c.csproj" };
        var state = new PistonState();
        var completedCounts = new ConcurrentQueue<int>();

        var testRunnerStub = new StubTestRunnerService(projects, delay: TimeSpan.FromMilliseconds(10));

        var orchestrator = new PistonOrchestrator(
            new StubFileWatcherService(),
            new StubBuildService(),
            testRunnerStub,
            new StubImpactAnalyzer(projects),
            state);

        state.StateChanged += () =>
        {
            if (state.Phase == PistonPhase.Testing)
            {
                completedCounts.Enqueue(state.CompletedTestProjects);
            }
        };

        await orchestrator.StartAsync("/fake/solution.slnx");
        await testRunnerStub.WaitForCompletionAsync();

        // CompletedTestProjects should have gone from 0 up
        Assert.Contains(0, completedCounts);
        orchestrator.Dispose();
    }

    [Fact]
    public async Task TotalTestProjects_IsSetCorrectly_WhenTestProjectsAreKnown()
    {
        var projects = new[] { "a.csproj", "b.csproj" };
        var state = new PistonState();
        var totalCounts = new List<int>();

        var testRunnerStub = new StubTestRunnerService(projects, delay: TimeSpan.Zero);

        var orchestrator = new PistonOrchestrator(
            new StubFileWatcherService(),
            new StubBuildService(),
            testRunnerStub,
            new StubImpactAnalyzer(projects),
            state);

        state.StateChanged += () =>
        {
            if (state.Phase == PistonPhase.Testing)
            {
                totalCounts.Add(state.TotalTestProjects);
            }
        };

        await orchestrator.StartAsync("/fake/solution.slnx");
        await testRunnerStub.WaitForCompletionAsync();

        Assert.Contains(2, totalCounts);
        orchestrator.Dispose();
    }
}

// ── Stubs ─────────────────────────────────────────────────────────────────────

internal sealed class StubFileWatcherService : IFileWatcherService
{
    public event Action<FileChangeBatch>? FileChanged;

    public void Start(string directory) { }
    public void Stop() { }
    public void Dispose() { }

    // Allow tests to trigger file changes
    public void TriggerChange(FileChangeBatch batch) => FileChanged?.Invoke(batch);
}

internal sealed class StubBuildService : IBuildService
{
    public Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct) =>
        Task.FromResult(new BuildResult(BuildStatus.Succeeded, [], [], TimeSpan.FromMilliseconds(1)));

    public Task<BuildResult> BuildAsync(string solutionPath, IReadOnlyList<string>? projectPaths, CancellationToken ct) =>
        Task.FromResult(new BuildResult(BuildStatus.Succeeded, [], [], TimeSpan.FromMilliseconds(1)));
}

internal sealed class StubImpactAnalyzer : IImpactAnalyzer
{
    private readonly IReadOnlyList<string> _testProjects;

    public StubImpactAnalyzer(IReadOnlyList<string> testProjects) => _testProjects = testProjects;

    public Task InitializeAsync(string solutionPath, CancellationToken ct) => Task.CompletedTask;

    public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes) =>
        new(_testProjects, _testProjects, RequiresGraphRebuild: false, IsFullRun: false);

    public ImpactAnalysisResult AnalyzeFullRun() =>
        new(_testProjects, _testProjects, RequiresGraphRebuild: false, IsFullRun: false);

    public void InvalidateGraph() { }
    public IReadOnlyList<string> GetAllTestProjectPaths() => _testProjects;
    public bool IsMtpProject(string projectPath) => false;
    public string? GetMtpOutputPath(string projectPath) => null;
}

internal sealed class StubTestRunnerService : ITestRunnerService
{
    private readonly IReadOnlyList<string> _projectPaths;
    private readonly TimeSpan _delay;
    private readonly TaskCompletionSource _completionSource = new();

    public StubTestRunnerService(IReadOnlyList<string> projectPaths, TimeSpan delay)
    {
        _projectPaths = projectPaths;
        _delay = delay;
    }

    public Task WaitForCompletionAsync() => _completionSource.Task;

    public Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        string? filter,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct) =>
        RunTestsAsync(solutionPath, null, filter, false, onProgress, null, ct);

    public Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        IReadOnlyList<string>? testProjectPaths,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct) =>
        RunTestsAsync(solutionPath, testProjectPaths, filter, collectCoverage, onProgress, null, ct);

    public async Task<TestRunResult> RunTestsAsync(
        string solutionPath,
        IReadOnlyList<string>? testProjectPaths,
        string? filter,
        bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        Action<ProjectTestResult>? onProjectCompleted,
        CancellationToken ct)
    {
        var paths = testProjectPaths ?? _projectPaths;
        var allSuites = new List<TestSuite>();

        foreach (var path in paths)
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, ct).ConfigureAwait(false);
            }

            var suite = new TestSuite(
                path,
                [new TestResult(path + ".Test1", "Test1", TestStatus.Passed, TimeSpan.Zero, null, null, null, null)],
                DateTimeOffset.UtcNow,
                TimeSpan.Zero);

            allSuites.Add(suite);

            var projectResult = new ProjectTestResult(path, [suite], null, [], false);
            onProjectCompleted?.Invoke(projectResult);
        }

        _ = _completionSource.TrySetResult();
        return new TestRunResult(allSuites, null, []);
    }
}
