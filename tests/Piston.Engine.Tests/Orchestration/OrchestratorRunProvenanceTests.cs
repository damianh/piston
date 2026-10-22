// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

/// <summary>
/// Deterministic tests for run-bound result provenance: run IDs, scope, filters, completion
/// state, duplicate identities and source-fingerprint freshness.
/// </summary>
public sealed class OrchestratorRunProvenanceTests : IDisposable
{
    private static readonly string[] Projects = ["/src/A.Tests/A.Tests.csproj", "/src/B.Tests/B.Tests.csproj"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-provenance-{Guid.NewGuid():N}");
    private readonly PistonState _state = new();
    private readonly StubFileWatcherService _watcher = new();
    private readonly ProvenanceBuildService _build = new();
    private readonly ProvenanceTestRunner _runner = new();
    private readonly ProvenanceImpactAnalyzer _impact = new(Projects);
    private readonly PistonOrchestrator _orchestrator;

    public OrchestratorRunProvenanceTests()
    {
        _ = Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "App.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Code { }");
        _orchestrator = new PistonOrchestrator(_watcher, _build, _runner, _impact, _state);
    }

    private string SolutionPath => Path.Combine(_root, "App.slnx");

    public void Dispose()
    {
        _orchestrator.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task StartAndWaitForStartupRunAsync()
    {
        await _orchestrator.StartAsync(SolutionPath);
        await WaitForAsync(() => _state.LastRun is { Trigger: TestRunTrigger.Startup });
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ActiveRunPublication_NotifiesBeforeGraphInitializationCompletes()
    {
        var releaseGraph = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _impact.Initialization = releaseGraph.Task;
        TestRunRecord? notified = null;
        _state.StateChanged += () => notified = _state.CurrentRun;
        try
        {
            await _orchestrator.StartAsync(SolutionPath);
            Assert.NotNull(notified);
            Assert.Equal(TestRunStatus.Running, notified.Status);
            Assert.Null(notified.Context);
            Assert.Null(_state.LastRun);
        }
        finally
        {
            _ = releaseGraph.TrySetResult();
            await WaitForAsync(() => _state.LastRun is not null);
        }
    }

    [Fact]
    public async Task RunContext_RecordsBuildAndMixedRunnerWorkingDirectories()
    {
        _ = _impact.MtpProjects.Add(Projects[1]);
        await StartAndWaitForStartupRunAsync();

        var run = await _orchestrator.RequestRunAsync();

        Assert.Equal(_root, run!.Context!.WorkingDirectory);
        Assert.Equal(TestProcessRunner.WorkingDirectoryFor(Projects[0]) ?? Environment.CurrentDirectory, run.Context.TestWorkingDirectories![Projects[0]]);
        Assert.Equal(_root, run.Context.TestWorkingDirectories[Projects[1]]);
    }

    [Fact]
    public async Task CancelledRun_CapturesPostRunFingerprintAndNotifiesTerminalState()
    {
        await StartAndWaitForStartupRunAsync();
        TestRunRecord? terminal = null;
        _state.StateChanged += () =>
        {
            if (_state.CurrentRun is null)
            {
                terminal = _state.LastRun;
            }
        };
        _runner.DuringRun = () =>
        {
            File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Changed { }");
            throw new OperationCanceledException();
        };

        var run = await _orchestrator.RequestRunAsync();

        Assert.Equal(TestRunStatus.Cancelled, run!.Status);
        Assert.Equal(ResultFreshness.Stale, run.Freshness);
        Assert.Equal(SourceFingerprinter.Compute(_root), run.SourceAfter);
        Assert.Same(run, terminal);
    }

    [Fact]
    public async Task FaultedRun_CapturesPostRunFingerprintAndNotifiesTerminalState()
    {
        await StartAndWaitForStartupRunAsync();
        TestRunRecord? terminal = null;
        _state.StateChanged += () =>
        {
            if (_state.CurrentRun is null)
            {
                terminal = _state.LastRun;
            }
        };
        _runner.DuringRun = () => throw new InvalidOperationException("runner fault");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => _orchestrator.RequestRunAsync());

        Assert.Equal(TestRunStatus.Faulted, terminal!.Status);
        Assert.Equal(SourceFingerprinter.Compute(_root), terminal.SourceAfter);
        Assert.False(terminal.IsCurrentFullResult);
    }

    [Fact]
    public async Task ForcedFullRun_IsCompleteCurrentAndBoundToSourceFingerprint()
    {
        await StartAndWaitForStartupRunAsync();

        var run = await _orchestrator.RequestRunAsync();

        Assert.NotNull(run);
        Assert.Equal(TestRunTrigger.Forced, run.Trigger);
        Assert.Equal(TestRunStatus.Completed, run.Status);
        Assert.Equal(TestRunScope.Full, run.Scope);
        Assert.False(run.IsFiltered);
        Assert.Equal(ResultFreshness.Current, run.Freshness);
        Assert.True(run.IsCurrentFullResult);
        Assert.Equal(SourceFingerprinter.Compute(_root), run.SourceBefore);
        Assert.Equal(run.SourceBefore, run.SourceAfter);
        Assert.Equal(Projects, run.Context!.TestProjects);
        Assert.Equal(["A.Tests.Test1", "B.Tests.Test1"], run.Tests.Select(t => t.FullyQualifiedName).Order());
        Assert.All(run.Suites, s => Assert.Equal(run.RunId, s.RunId));
        Assert.All(run.Projects, p => Assert.Equal(ProjectRunStatus.Completed, p.Status));
        Assert.Same(run, _state.LastRun);
        Assert.Null(_state.CurrentRun);
    }

    [Fact]
    public async Task SourceMutatedDuringTestExecution_IsStaleAndNotCurrent()
    {
        await StartAndWaitForStartupRunAsync();
        _runner.DuringRun = () => File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Code { int x; }");

        var run = await _orchestrator.RequestRunAsync();

        Assert.NotNull(run);
        Assert.Equal(TestRunStatus.Completed, run.Status);
        Assert.Equal(ResultFreshness.Stale, run.Freshness);
        Assert.NotEqual(run.SourceBefore, run.SourceAfter);
        Assert.Equal(SourceFingerprinter.Compute(_root), run.SourceAfter);
        Assert.False(run.IsCurrentFullResult);
    }

    [Fact]
    public async Task WatcherChangeDuringRun_CancelsAndMarksStaleEvenIfContentIsRestored()
    {
        await StartAndWaitForStartupRunAsync();
        _runner.DuringRun = () =>
        {
            _runner.DuringRun = null;
            _watcher.TriggerChange(new FileChangeBatch(
                [new FileChangeEvent(Path.Combine(_root, "Code.cs"), WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)], DateTimeOffset.UtcNow));
        };

        var run = await _orchestrator.RequestRunAsync();

        Assert.NotNull(run);
        Assert.Equal(TestRunStatus.Cancelled, run.Status);
        Assert.Equal(ResultFreshness.Stale, run.Freshness);
        Assert.True(run.SourceChangeObserved);
        Assert.False(run.IsCurrentFullResult);
    }

    [Fact]
    public async Task SelectiveRun_CannotSatisfyFullScope_AndDoesNotReplaceRunProvenance()
    {
        await StartAndWaitForStartupRunAsync();
        var full = await _orchestrator.RequestRunAsync();

        _watcher.TriggerChange(new FileChangeBatch(
            [new FileChangeEvent(Path.Combine(_root, "Code.cs"), WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)], DateTimeOffset.UtcNow));
        await WaitForAsync(() => _state.LastRun is { Trigger: TestRunTrigger.FileChange });
        var selective = _state.LastRun!;

        Assert.Equal(TestRunStatus.Completed, selective.Status);
        Assert.Equal(TestRunScope.Selective, selective.Scope);
        Assert.False(selective.IsFullScope);
        Assert.False(selective.IsCurrentFullResult);
        Assert.Equal(["A.Tests.Test1"], selective.Tests.Select(t => t.FullyQualifiedName));

        // The aggregate still shows both projects, but each suite is attributed to the run that produced it.
        Assert.Equal(2, _state.TestSuites.Count);
        Assert.Contains(_state.TestSuites, s => s.RunId == full!.RunId);
        Assert.Contains(_state.TestSuites, s => s.RunId == selective.RunId);
    }

    [Fact]
    public async Task UserFilter_MarksFullRunAsFilteredAndNotFullScope()
    {
        await StartAndWaitForStartupRunAsync();
        _state.TestFilter = "FullyQualifiedName~A";
        _ = _runner.ProjectsWithoutResults.Add(Projects[1]);

        var run = await _orchestrator.RequestRunAsync();

        Assert.NotNull(run);
        // A filter that selects nothing in a project is not a failure, but the run is still ineligible.
        Assert.Equal(TestRunStatus.Completed, run.Status);
        Assert.Equal(TestRunScope.Full, run.Scope);
        Assert.True(run.IsFiltered);
        Assert.False(run.IsFullScope);
        Assert.False(run.IsCurrentFullResult);
        Assert.Equal("FullyQualifiedName~A", run.Context!.EffectiveFilter);
        Assert.Equal("FullyQualifiedName~A", _runner.LastFilter);
    }

    [Fact]
    public async Task EachRequestGetsANewRunId_AndPriorRunsKeepTheirOwnResults()
    {
        await StartAndWaitForStartupRunAsync();
        var startup = _state.LastRun!;

        var first = await _orchestrator.RequestRunAsync();
        _runner.FailingTest = "B.Tests.Test1";
        var second = await _orchestrator.RequestRunAsync();

        Assert.Equal(3, new[] { startup.RunId, first!.RunId, second!.RunId }.Distinct().Count());
        Assert.True(second.Sequence > first.Sequence);
        Assert.Equal(first.EngineInstanceId, second.EngineInstanceId);
        Assert.All(first.Tests, t => Assert.Equal(TestStatus.Passed, t.Status));
        Assert.Contains(second.Tests, t => t is { FullyQualifiedName: "B.Tests.Test1", Status: TestStatus.Failed });
        Assert.Same(first, _state.FindRun(first.RunId));
        Assert.All(_state.TestSuites, s => Assert.Equal(second.RunId, s.RunId));
    }

    [Fact]
    public async Task ProjectWithoutResults_MarksRunIncomplete()
    {
        await StartAndWaitForStartupRunAsync();
        _ = _runner.ProjectsWithoutResults.Add(Projects[1]);

        var run = await _orchestrator.RequestRunAsync();

        Assert.Equal(TestRunStatus.Incomplete, run!.Status);
        Assert.Contains("B.Tests", run.StatusReason);
        Assert.Contains("diagnostics.log", run.StatusReason, StringComparison.Ordinal);
        Assert.Contains(run.Projects, p => p.NoTestsReason is not null);
        Assert.False(run.IsCurrentFullResult);
    }

    [Fact]
    public async Task TimedOutProject_MarksRunIncompleteWithTheSlowTest()
    {
        await StartAndWaitForStartupRunAsync();
        _runner.TimedOutProjects[Projects[1]] = new TestTimeout("B.Tests.Slow", TimeSpan.FromSeconds(30));

        var run = await _orchestrator.RequestRunAsync();

        Assert.Equal(TestRunStatus.Incomplete, run!.Status);
        Assert.Contains("B.Tests: timed out (B.Tests.Slow ran longer than the test timeout of 30 s)", run.StatusReason, StringComparison.Ordinal);
        var outcome = Assert.Single(run.Projects, p => p.Status == ProjectRunStatus.TimedOut);
        Assert.Equal("B.Tests.Slow", outcome.TimedOut?.Test);
    }

    [Fact]
    public async Task TestRunningPastTheHangThreshold_IsReportedAsPossiblyHanging()
    {
        await StartAndWaitForStartupRunAsync();
        _orchestrator.HangThreshold = TimeSpan.FromMilliseconds(50);
        _orchestrator.RunningTestCheckInterval = TimeSpan.FromMilliseconds(10);
        _runner.RunningTest = "A.Tests.Slow";
        _runner.HoldRunning = TimeSpan.FromMilliseconds(400);
        RunningTest? seen = null;
        _state.StateChanged += () => seen = _state.LongestRunningTest is { PossiblyHanging: true } hanging ? hanging : seen;

        var run = await _orchestrator.RequestRunAsync();

        Assert.Equal(["A.Tests.Slow"], run!.PossiblyHangingTests);
        Assert.Equal("A.Tests.Slow", seen?.Name);
        Assert.Null(_state.LongestRunningTest);
    }

    [Fact]
    public async Task CrashedProject_MarksRunIncomplete()
    {
        await StartAndWaitForStartupRunAsync();
        _ = _runner.CrashedProjects.Add(Projects[0]);

        var run = await _orchestrator.RequestRunAsync();

        Assert.Equal(TestRunStatus.Incomplete, run!.Status);
        Assert.Contains(run.Projects, p => p.Status == ProjectRunStatus.Crashed);
        Assert.False(run.IsCurrentFullResult);
    }

    [Fact]
    public async Task DuplicateTestIdentities_AreReportedAndNotCurrentFullResult()
    {
        await StartAndWaitForStartupRunAsync();
        _runner.DuplicateFirstTest = true;

        var run = await _orchestrator.RequestRunAsync();

        Assert.Equal(TestRunStatus.Completed, run!.Status);
        Assert.Equal(["A.Tests.Test1"], run.DuplicateTestIds);
        Assert.False(run.IsCurrentFullResult);
    }

    [Fact]
    public async Task BuildFailure_IsTerminalWithoutTests()
    {
        await StartAndWaitForStartupRunAsync();
        _build.Fail = true;
        TestRunRecord? terminal = null;
        _state.StateChanged += () =>
        {
            if (_state.CurrentRun is null)
            {
                terminal = _state.LastRun;
            }
        };

        var run = await _orchestrator.RequestRunAsync();

        Assert.Equal(TestRunStatus.BuildFailed, run!.Status);
        Assert.Equal(BuildStatus.Failed, run.Build!.Status);
        Assert.Equal(["CS0001: boom"], run.Build.Errors);
        Assert.Equal(ResultFreshness.Current, run.Freshness);
        Assert.Empty(run.Tests);
        Assert.False(run.IsCurrentFullResult);
        Assert.Same(run, terminal);
        Assert.Equal(SourceFingerprinter.Compute(_root), run.SourceAfter);
    }

    [Fact]
    public async Task BuildDiagnosticsStayWithTheirRunAfterLaterBuilds()
    {
        await StartAndWaitForStartupRunAsync();
        _build.Fail = true;
        var failed = await _orchestrator.RequestRunAsync();
        _build.Errors.Clear();
        _build.Fail = false;
        var succeeded = await _orchestrator.RequestRunAsync();

        Assert.NotEqual(failed!.RunId, succeeded!.RunId);
        Assert.Equal(BuildStatus.Succeeded, _state.LastBuild!.Status);
        Assert.Empty(succeeded.Build!.Errors);
        Assert.Equal(["CS0001: boom"], _state.FindRun(failed.RunId)!.Build!.Errors);
    }

    [Fact]
    public async Task BuildFailureWithSourceMutationIsStale()
    {
        await StartAndWaitForStartupRunAsync();
        _build.Fail = true;
        _build.DuringBuild = () => File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Changed { }");
        var run = await _orchestrator.RequestRunAsync();

        Assert.Equal(TestRunStatus.BuildFailed, run!.Status);
        Assert.Equal(ResultFreshness.Stale, run.Freshness);
        Assert.NotEqual(run.SourceBefore, run.SourceAfter);
        Assert.Equal(["CS0001: boom"], run.Build!.Errors);
    }
}

internal sealed class ProvenanceImpactAnalyzer(IReadOnlyList<string> testProjects) : IImpactAnalyzer
{
    public Func<string?> VerificationCoverage { get; set; } = () => null;
    public string? GetVerificationCoverageIssue(string solutionPath) => VerificationCoverage();
    public Task Initialization { get; set; } = Task.CompletedTask;
    public HashSet<string> MtpProjects { get; } = [];
    public Task InitializeAsync(string solutionPath, CancellationToken ct) => Initialization;

    public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes) =>
        new([testProjects[0]], [testProjects[0]], RequiresGraphRebuild: false, IsFullRun: false);

    public ImpactAnalysisResult AnalyzeFullRun() =>
        new(testProjects, testProjects, RequiresGraphRebuild: false, IsFullRun: true);

    public void InvalidateGraph() { }
    public IReadOnlyList<string> GetAllTestProjectPaths() => testProjects;
    public bool IsMtpProject(string projectPath) => MtpProjects.Contains(projectPath);
    public string? GetMtpOutputPath(string projectPath) => IsMtpProject(projectPath) ? projectPath + ".dll" : null;
}

internal sealed class ProvenanceBuildService : IBuildService
{
    public bool Fail { get; set; }
    public Action? DuringBuild { get; set; }
    public List<string> Errors { get; } = ["CS0001: boom"];

    public Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct) => BuildAsync(solutionPath, null, ct);

    public Task<BuildResult> BuildAsync(string solutionPath, IReadOnlyList<string>? projectPaths, CancellationToken ct)
    {
        DuringBuild?.Invoke();
        return Task.FromResult(Fail
            ? new BuildResult(BuildStatus.Failed, Errors, [], TimeSpan.FromMilliseconds(1))
            : new BuildResult(BuildStatus.Succeeded, [], [], TimeSpan.FromMilliseconds(1)));
    }
}

internal sealed class ProvenanceTestRunner : ITestRunnerService
{
    public Action? DuringRun { get; set; }
    public string? FailingTest { get; set; }
    public bool DuplicateFirstTest { get; set; }
    public HashSet<string> ProjectsWithoutResults { get; } = [];
    public HashSet<string> CrashedProjects { get; } = [];
    public Dictionary<string, TestTimeout> TimedOutProjects { get; } = [];
    public string? RunningTest { get; set; }
    public TimeSpan HoldRunning { get; set; }
    public string? LastFilter { get; private set; }

    public Task<TestRunResult> RunTestsAsync(
        string solutionPath, string? filter, Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
        RunTestsAsync(solutionPath, null, filter, false, onProgress, null, ct);

    public Task<TestRunResult> RunTestsAsync(
        string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
        RunTestsAsync(solutionPath, testProjectPaths, filter, collectCoverage, onProgress, null, ct);

    public async Task<TestRunResult> RunTestsAsync(
        string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress, Action<ProjectTestResult>? onProjectCompleted, CancellationToken ct)
    {
        LastFilter = filter;
        DuringRun?.Invoke();

        if (RunningTest is { } running)
        {
            onProgress?.Invoke([new TestSuite("A.Tests",
                [new TestResult(running, running, TestStatus.Running, TimeSpan.Zero, null, null, null, null)],
                DateTimeOffset.UtcNow, TimeSpan.Zero)]);
            await Task.Delay(HoldRunning, ct);
        }

        var all = new List<TestSuite>();
        foreach (var path in testProjectPaths ?? [])
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (CrashedProjects.Contains(path))
            {
                onProjectCompleted?.Invoke(new ProjectTestResult(path, [], "crashed", [], Crashed: true));
                continue;
            }
            if (TimedOutProjects.TryGetValue(path, out var timeout))
            {
                var timedOutTest = new TestResult(timeout.Test, timeout.Test, TestStatus.Failed, timeout.Limit, null, "Timed out", null, null);
                var timedOutSuite = new TestSuite(name, [timedOutTest], DateTimeOffset.UtcNow, timeout.Limit);
                all.Add(timedOutSuite);
                onProjectCompleted?.Invoke(new ProjectTestResult(path, [timedOutSuite], null, [], Crashed: false) { TimedOut = timeout });
                continue;
            }
            if (ProjectsWithoutResults.Contains(path))
            {
                onProjectCompleted?.Invoke(new ProjectTestResult(path, [], null, [], Crashed: false));
                continue;
            }

            var fqn = $"{name}.Test1";
            var tests = new List<TestResult>
            {
                new(fqn, "Test1", fqn == FailingTest ? TestStatus.Failed : TestStatus.Passed,
                    TimeSpan.FromMilliseconds(1), null, null, null, null),
            };
            if (DuplicateFirstTest && all.Count == 0)
            {
                tests.Add(tests[0]);
            }

            var suite = new TestSuite(name, tests, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(1));
            all.Add(suite);
            onProjectCompleted?.Invoke(new ProjectTestResult(path, [suite], null, [], Crashed: false));
        }

        return new TestRunResult(all, null, []);
    }
}
