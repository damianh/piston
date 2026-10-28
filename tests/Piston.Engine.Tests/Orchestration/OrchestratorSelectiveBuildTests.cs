// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class OrchestratorSelectiveBuildTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-selective-{Guid.NewGuid():N}");

    private const string Lib = "/repo/Lib/Lib.csproj";
    private const string LibTests = "/repo/Lib.Tests/Lib.Tests.csproj";
    private const string LibTestsId = "Lib.Tests/Lib.Tests.csproj";

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
            new TestSuite("@machine 10:00:00", [T("Lib.T.A", TestStatus.Passed), T("Lib.T.B", TestStatus.Passed)], DateTimeOffset.UtcNow, TimeSpan.Zero) { ProjectId = LibTestsId },
            new TestSuite("@machine 10:00:01", [T("Other.T.C", TestStatus.Passed)], DateTimeOffset.UtcNow, TimeSpan.Zero),
        };
        var rerun = new[]
        {
            new TestSuite("@machine 10:05:00", [T("Lib.T.A", TestStatus.Failed)], DateTimeOffset.UtcNow, TimeSpan.Zero) { ProjectId = LibTestsId },
        };

        var merged = PistonOrchestrator.MergeTestSuites(existing, rerun).SelectMany(s => s.Tests).ToList();

        Assert.Equal(3, merged.Count);
        Assert.Equal(TestStatus.Failed, Assert.Single(merged, t => t.FullyQualifiedName == "Lib.T.A").Status);
        Assert.Contains(merged, t => t.FullyQualifiedName == "Lib.T.B" && t.Status == TestStatus.Passed);
        Assert.Contains(merged, t => t.FullyQualifiedName == "Other.T.C" && t.Status == TestStatus.Passed);
    }

    [Fact]
    public void MergeTestSuites_PartialRerunsKeepOnlyLatestLogPerProject()
    {
        static TestResult T(string fqn) =>
            new(fqn, fqn, TestStatus.Passed, TimeSpan.Zero, null, null, null, null);

        var untouched = T("Lib.T.Untouched") with { Output = "retained output" };
        var otherLog = new ExecutionLog("/repo/Other.Tests/Other.Tests.csproj");
        IReadOnlyList<TestSuite> merged =
        [
            new TestSuite("original", [T("Lib.T.A"), T("Lib.T.B"), untouched], DateTimeOffset.UtcNow, TimeSpan.Zero)
            {
                ProjectId = LibTestsId,
                ExecutionLog = new ExecutionLog(LibTests),
            },
            new TestSuite("Other.Tests", [T("Other.T.C")], DateTimeOffset.UtcNow, TimeSpan.Zero)
            {
                ExecutionLog = otherLog,
            },
        ];

        for (var i = 0; i < PistonState.MaxRecentRuns + 2; i++)
        {
            var latestLog = new ExecutionLog(LibTests);
            merged = PistonOrchestrator.MergeTestSuites(merged,
                [new TestSuite($"partial-{i}", [T(i % 2 == 0 ? "Lib.T.A" : "Lib.T.B")],
                    DateTimeOffset.UtcNow, TimeSpan.Zero) { ProjectId = LibTestsId, ExecutionLog = latestLog }]);

            var retainedLog = Assert.Single(merged, s => s.ExecutionLog?.ProjectPath == LibTests);
            Assert.Same(latestLog, retainedLog.ExecutionLog);
            Assert.Same(otherLog, merged.Single(s => s.Name == "Other.Tests").ExecutionLog);
            Assert.Equal(4, merged.Sum(s => s.Tests.Count));
            Assert.Same(untouched, merged.SelectMany(s => s.Tests).Single(t => t.FullyQualifiedName == untouched.FullyQualifiedName));
        }
    }

    [Fact]
    public void MergeTestSuites_FrameworkResultReplacesOnlyThatFramework()
    {
        static TestResult T(string fqn, string? framework, TestStatus status) =>
            new(fqn, fqn, status, TimeSpan.Zero, null, null, null, null) { TargetFramework = framework };

        var existing = new[]
        {
            new TestSuite("Lib.Tests", [T("Lib.T.A", "net8.0", TestStatus.Passed), T("Lib.T.A", "net10.0", TestStatus.Passed)], DateTimeOffset.UtcNow, TimeSpan.Zero),
        };

        var oneFramework = PistonOrchestrator.MergeTestSuites(
            existing,
            [new TestSuite("Lib.Tests", [T("Lib.T.A", "net10.0", TestStatus.Failed)], DateTimeOffset.UtcNow, TimeSpan.Zero)])
            .SelectMany(s => s.Tests).ToList();
        Assert.Equal(TestStatus.Passed, Assert.Single(oneFramework, t => t.TargetFramework == "net8.0").Status);
        Assert.Equal(TestStatus.Failed, Assert.Single(oneFramework, t => t.TargetFramework == "net10.0").Status);

        var anyFramework = PistonOrchestrator.MergeTestSuites(
            existing,
            [new TestSuite("Lib.Tests", [T("Lib.T.A", null, TestStatus.Failed)], DateTimeOffset.UtcNow, TimeSpan.Zero)])
            .SelectMany(s => s.Tests).ToList();
        Assert.Equal(TestStatus.Failed, Assert.Single(anyFramework).Status);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("net10.0", "net10.0")]
    [InlineData("net10.0", null)]
    [InlineData(null, "net10.0")]
    public void MergeTestSuites_SharedTestNamesOnlyReplaceResultsInTheirOwnProject(string? oldFramework, string? newFramework)
    {
        var oldResult = new TestResult("Shared.Test", "Shared.Test", TestStatus.Passed, TimeSpan.Zero, null, null, null, null)
        {
            TargetFramework = oldFramework,
        };
        var first = new TestSuite("Tests", [oldResult], DateTimeOffset.UnixEpoch, TimeSpan.Zero)
        {
            ProjectId = "One/Tests.csproj",
        };
        var second = first with { ProjectId = "Two/Tests.csproj", RunId = "untouched" };
        var rerun = first with { Tests = [oldResult with { Status = TestStatus.Failed, TargetFramework = newFramework }] };

        var merged = PistonOrchestrator.MergeTestSuites([first, second], [rerun]);

        Assert.Equal(2, merged.Count);
        var untouched = Assert.Single(merged, s => s.Key == second.Key);
        Assert.Equal(second.Tests, untouched.Tests);
        Assert.Equal(second.RunId, untouched.RunId);
        Assert.Equal(TestStatus.Failed, Assert.Single(Assert.Single(merged, s => s.Key == first.Key).Tests).Status);
    }

    [Fact]
    public async Task RunSelected_PreservesTheOtherProjectsSharedTestAndRunLog()
    {
        const string firstPath = "/repo/One/Tests.csproj";
        const string secondPath = "/repo/Two/Tests.csproj";
        var result = new TestResult("Shared.Test", "Shared.Test", TestStatus.Passed, TimeSpan.Zero, null, null, null, null);
        var suite = new TestSuite("Tests", [result], DateTimeOffset.UnixEpoch, TimeSpan.Zero);
        var runner = new RecordingTestRunner
        {
            ResultsByProject = new() { [firstPath] = [suite], [secondPath] = [suite] },
        };
        var state = new PistonState();
        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), new RecordingBuildService(), runner,
            new FixedImpactAnalyzer(new([], [], false, false)) { TestProjects = [firstPath, secondPath] }, state);
        await orchestrator.StartAsync("/repo/All.slnx");
        await WaitForAsync(() => state.LastRun is not null);
        var untouched = Assert.Single(state.TestSuites, s => s.Key == "Two/Tests.csproj");

        var run = await orchestrator.RunSelectedAsync(new TestSelection(["One/Tests.csproj"], ["Shared.Test"]));

        Assert.Equal(2, state.TestSuites.Count);
        var preserved = Assert.Single(state.TestSuites, s => s.Key == "Two/Tests.csproj");
        Assert.Equal(untouched.Tests, preserved.Tests);
        Assert.Equal(untouched.RunId, preserved.RunId);
        Assert.NotEqual(run.RunId, untouched.RunId);
        Assert.NotNull(state.FindRun(untouched.RunId!));
    }

    [Fact]
    public async Task SolutionFallback_PreservesDistinctSuiteIdentitiesDuringAndAfterTheRun()
    {
        var test = new TestResult("Shared.Test", "Shared.Test", TestStatus.Passed, TimeSpan.Zero, null, null, null, null);
        TestSuite[] suites =
        [
            new("One", [test], DateTimeOffset.UnixEpoch, TimeSpan.Zero),
            new("Two", [test], DateTimeOffset.UnixEpoch, TimeSpan.Zero),
        ];
        var state = new PistonState();
        var overlays = new List<IReadOnlyList<TestSuite>>();
        state.StateChanged += () =>
        {
            if (state.InProgressSuites.Count > 0)
            {
                overlays.Add(state.InProgressSuites);
            }
        };
        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), new RecordingBuildService(),
            new RecordingTestRunner { ResultsByProject = new() { ["/repo/All.slnx"] = suites } },
            new FixedImpactAnalyzer(new([], [], false, false)) { TestProjects = [] }, state);

        await orchestrator.StartAsync("/repo/All.slnx");
        await WaitForAsync(() => state.LastRun is not null);

        Assert.Equal(["One", "Two"], state.LastRun!.Suites.Select(s => s.Key));
        Assert.NotEmpty(overlays);
        Assert.All(overlays, overlay => Assert.Equal(["One", "Two"], overlay.Select(s => s.Key)));
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
        {
            await Task.Delay(10, _ct);
        }

        Assert.Equal(PistonPhase.Watching, state.Phase);
        Assert.Equal([Lib], build.Calls[1]);
        _ = Assert.Single(runner.Calls); // only the initial full run executed tests
    }

    [Fact]
    public async Task RunSelected_BuildsAndRunsTheSelection_IgnoringTheUserFilter()
    {
        var state = new PistonState { TestFilter = "Category=Slow" };
        var build = new RecordingBuildService();
        var runner = new RecordingTestRunner();
        var analyzer = new FixedImpactAnalyzer(
            new ImpactAnalysisResult([], [], RequiresGraphRebuild: false, IsFullRun: false));

        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), build, runner, analyzer, state);
        await orchestrator.StartAsync("/repo/All.slnx");
        await runner.WaitForCallsAsync(1);

        var run = await orchestrator.RunSelectedAsync(
            new TestSelection([LibTestsId], ["Lib.T.A(x: 1)", "Lib.T.A(x: 2)", "Lib.T.B"]));

        Assert.Equal(TestRunTrigger.Targeted, run.Trigger);
        Assert.Equal(TestRunScope.Selective, run.Scope);
        Assert.Null(run.Context!.UserFilter);
        Assert.Equal("FullyQualifiedName=Lib.T.A|FullyQualifiedName=Lib.T.B", run.Context.EffectiveFilter);
        Assert.Equal([LibTests], build.Calls[1]);
        Assert.Equal([LibTests], runner.Calls[1]);
        Assert.Equal(run.Context.EffectiveFilter, runner.Filters[1]);
    }

    [Fact]
    public async Task RunSelected_TestWithACustomDisplayName_IsFilteredByItsMethod()
    {
        var state = new PistonState();
        var runner = new RecordingTestRunner();
        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), new RecordingBuildService(),
            runner, new FixedImpactAnalyzer(new([], [], false, false)), state);
        await orchestrator.StartAsync("/repo/All.slnx");
        await runner.WaitForCallsAsync(1);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (state.LastRun is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, _ct);
        }

        state.TestSuites =
        [
            new TestSuite("Lib.Tests",
                [new("Lib.T.nice name", "nice name", TestStatus.Passed, TimeSpan.Zero, null, null, null, null) { Method = "Lib.T.M" }],
                DateTimeOffset.UtcNow, TimeSpan.Zero) { ProjectId = LibTestsId },
        ];

        var run = await orchestrator.RunSelectedAsync(new TestSelection([LibTestsId], ["Lib.T.nice name"]));

        Assert.Equal("FullyQualifiedName=Lib.T.M", run.Context!.EffectiveFilter);
    }

    [Fact]
    public async Task RunSelected_TestNameWithADoubleQuote_IsRejected()
    {
        var runner = new RecordingTestRunner();
        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), new RecordingBuildService(),
            runner, new FixedImpactAnalyzer(new([], [], false, false)), new PistonState());
        await orchestrator.StartAsync("/repo/All.slnx");
        await runner.WaitForCallsAsync(1);

        _ = await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.RunSelectedAsync(
            new TestSelection([LibTestsId], ["X\" --settings \"/tmp/extra.runsettings"])));
        _ = Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task RunSelected_WholeProject_RunsWithoutAFilter()
    {
        var runner = new RecordingTestRunner();
        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), new RecordingBuildService(),
            runner, new FixedImpactAnalyzer(new([], [], false, false)), new PistonState());
        await orchestrator.StartAsync("/repo/All.slnx");
        await runner.WaitForCallsAsync(1);

        var run = await orchestrator.RunSelectedAsync(new TestSelection([LibTestsId]));

        Assert.Null(run.Context!.EffectiveFilter);
        Assert.Null(runner.Filters[1]);
    }

    [Fact]
    public async Task RunSelected_ProjectsSharingAName_RunsOnlyTheSelectedProject()
    {
        const string otherTests = "/repo/Other/Lib.Tests.csproj";
        var runner = new RecordingTestRunner();
        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), new RecordingBuildService(),
            runner, new FixedImpactAnalyzer(new([], [], false, false)) { TestProjects = [LibTests, otherTests] },
            new PistonState());
        await orchestrator.StartAsync("/repo/All.slnx");
        await runner.WaitForCallsAsync(1);

        _ = await orchestrator.RunSelectedAsync(new TestSelection([LibTestsId], ["Lib.T.A"]));

        Assert.Equal([LibTests], runner.Calls[1]);
        var otherProjectId = "Other/Lib.Tests.csproj";
        _ = await orchestrator.RunSelectedAsync(new TestSelection([otherProjectId], ["Lib.T.A"]));
        Assert.Equal([otherTests], runner.Calls[2]);
    }

    [Fact]
    public async Task RunSelected_ShortNameIsRejected()
    {
        var runner = new RecordingTestRunner();
        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), new RecordingBuildService(),
            runner, new FixedImpactAnalyzer(new([], [], false, false)) { TestProjects = [LibTests] }, new PistonState());
        await orchestrator.StartAsync("/repo/All.slnx");
        await runner.WaitForCallsAsync(1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            orchestrator.RunSelectedAsync(new TestSelection(["Lib.Tests"])));
        Assert.Contains("not a test project", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSelected_UnknownProject_IsRejected()
    {
        var runner = new RecordingTestRunner();
        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), new RecordingBuildService(),
            runner, new FixedImpactAnalyzer(new([], [], false, false)), new PistonState());
        await orchestrator.StartAsync("/repo/All.slnx");
        await runner.WaitForCallsAsync(1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.RunSelectedAsync(new TestSelection(["Nope.Tests/Nope.Tests.csproj"])));
        Assert.Contains("Nope.Tests", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSelected_WhileARunIsActive_IsRejectedWithoutCancellingIt()
    {
        var runner = new RecordingTestRunner { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), new RecordingBuildService(),
            runner, new FixedImpactAnalyzer(new([], [], false, false)), new PistonState());
        await orchestrator.StartAsync("/repo/All.slnx");
        await runner.WaitForCallsAsync(1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.RunSelectedAsync(new TestSelection([LibTestsId])));
        Assert.Contains("already in progress", ex.Message, StringComparison.Ordinal);
        Assert.False(runner.LastToken.IsCancellationRequested);

        runner.Gate.SetResult();
    }

    [Fact]
    public async Task RunSelected_SourcesUnchangedSinceASuccessfulBuild_ReusesTheBuild()
    {
        var state = new PistonState();
        var build = new RecordingBuildService();
        var runner = new RecordingTestRunner();
        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), build, runner,
            TempAnalyzer(new([], [], false, false)), state);
        await StartInTempRepoAsync(orchestrator, state);

        var first = await orchestrator.RunSelectedAsync(new TestSelection([LibTestsId], ["Lib.T.A"]));
        var second = await orchestrator.RunSelectedAsync(new TestSelection([LibTestsId]));

        _ = Assert.Single(build.Calls);
        Assert.Equal(BuildStatus.Skipped, first.Build!.Status);
        Assert.Equal(BuildStatus.Skipped, second.Build!.Status);
        Assert.Equal([TempLibTests], runner.Calls[2]);
    }

    [Fact]
    public async Task RunSelected_AfterASourceEdit_Builds()
    {
        var state = new PistonState();
        var build = new RecordingBuildService();
        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), build, new RecordingTestRunner(),
            TempAnalyzer(new([], [], false, false)), state);
        await StartInTempRepoAsync(orchestrator, state);

        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Code { int X; }");
        var run = await orchestrator.RunSelectedAsync(new TestSelection([LibTestsId]));

        Assert.Equal(2, build.Calls.Count);
        Assert.Equal(BuildStatus.Succeeded, run.Build!.Status);
    }

    [Fact]
    public async Task RunSelected_AfterAFailedBuild_Builds()
    {
        var state = new PistonState();
        var build = new RecordingBuildService { Status = BuildStatus.Failed };
        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), build, new RecordingTestRunner(),
            TempAnalyzer(new([], [], false, false)), state);
        await StartInTempRepoAsync(orchestrator, state);

        build.Status = BuildStatus.Succeeded;
        var run = await orchestrator.RunSelectedAsync(new TestSelection([LibTestsId]));

        Assert.Equal(2, build.Calls.Count);
        Assert.Equal(BuildStatus.Succeeded, run.Build!.Status);
    }

    [Fact]
    public async Task RunSelected_WhenTheLastBuildDidNotCoverTheProject_Builds()
    {
        var state = new PistonState();
        var watcher = new StubFileWatcherService();
        var build = new RecordingBuildService();
        using var orchestrator = new PistonOrchestrator(watcher, build, new RecordingTestRunner(),
            TempAnalyzer(new([Lib], [], false, false)), state);
        await StartInTempRepoAsync(orchestrator, state);

        watcher.TriggerChange(new FileChangeBatch(
            [new FileChangeEvent(Path.Combine(_root, "Code.cs"), WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow));
        await WaitForAsync(() => state.LastRun is { Trigger: TestRunTrigger.FileChange });

        var run = await orchestrator.RunSelectedAsync(new TestSelection([LibTestsId]));

        Assert.Equal([Lib], build.Calls[1]);
        Assert.Equal([TempLibTests], build.Calls[2]);
        Assert.Equal(BuildStatus.Succeeded, run.Build!.Status);
    }

    [Fact]
    public async Task RunSelected_WhenTheTestOutputIsMissing_Builds()
    {
        var state = new PistonState();
        var build = new RecordingBuildService();
        using var orchestrator = new PistonOrchestrator(new StubFileWatcherService(), build, new RecordingTestRunner(),
            TempAnalyzer(new([], [], false, false), Path.Combine(_root, "bin", "Lib.Tests.dll")),
            state);
        await StartInTempRepoAsync(orchestrator, state);

        var run = await orchestrator.RunSelectedAsync(new TestSelection([LibTestsId]));

        Assert.Equal(2, build.Calls.Count);
        Assert.Equal(BuildStatus.Succeeded, run.Build!.Status);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task StartInTempRepoAsync(PistonOrchestrator orchestrator, PistonState state)
    {
        _ = Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "All.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Code { }");
        await orchestrator.StartAsync(Path.Combine(_root, "All.slnx"));
        await WaitForAsync(() => state.LastRun is { Trigger: TestRunTrigger.Startup });
    }

    private string TempLibTests => Path.Combine(_root, "Lib.Tests", "Lib.Tests.csproj");

    private FixedImpactAnalyzer TempAnalyzer(ImpactAnalysisResult selective) =>
        TempAnalyzer(selective, null);

    private FixedImpactAnalyzer TempAnalyzer(ImpactAnalysisResult selective, string? mtpOutputPath) =>
        new(selective)
        {
            TestProjects = [TempLibTests],
            MtpOutputPath = mtpOutputPath,
        };

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

    private sealed class FixedImpactAnalyzer(ImpactAnalysisResult selective) : IImpactAnalyzer
    {
        public string? MtpOutputPath { get; init; }
        public IReadOnlyList<string> TestProjects { get; init; } = [LibTests];
        public Task InitializeAsync(string solutionPath, CancellationToken ct) => Task.CompletedTask;
        public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes) => selective;
        public ImpactAnalysisResult AnalyzeFullRun() => new([], [], RequiresGraphRebuild: false, IsFullRun: true);
        public void InvalidateGraph() { }
        public IReadOnlyList<string> GetAllTestProjectPaths() => TestProjects;
        public bool IsMtpProject(string projectPath) => false;
        public string? GetMtpOutputPath(string projectPath) => MtpOutputPath;
    }

    private sealed class RecordingBuildService : IBuildService
    {
        private readonly CallCounter _counter = new();
        public BuildStatus Status { get; set; } = BuildStatus.Succeeded;
        public List<IReadOnlyList<string>?> Calls { get; } = [];

        public Task WaitForCallsAsync(int count) => _counter.WaitForAsync(count);

        public Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct) =>
            BuildAsync(solutionPath, null, ct);

        public Task<BuildResult> BuildAsync(string solutionPath, IReadOnlyList<string>? projectPaths, CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add(projectPaths?.ToList());
            }

            _counter.Increment();
            return Task.FromResult(new BuildResult(Status, [], [], TimeSpan.Zero));
        }
    }

    private sealed class RecordingTestRunner : ITestRunnerService
    {
        private readonly CallCounter _counter = new();
        public List<IReadOnlyList<string>?> Calls { get; } = [];
        public List<string?> Filters { get; } = [];
        public TaskCompletionSource? Gate { get; init; }
        public CancellationToken LastToken { get; private set; }
        public Dictionary<string, IReadOnlyList<TestSuite>> ResultsByProject { get; init; } = [];

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
            lock (Calls)
            {
                Calls.Add(testProjectPaths?.ToList());
                Filters.Add(filter);
                LastToken = ct;
            }

            _counter.Increment();
            if (ResultsByProject.Count > 0)
            {
                var projects = (testProjectPaths ?? [solutionPath])
                    .Select(path => new ProjectTestResult(path, ResultsByProject[path], null, [], Crashed: false)).ToList();
                var suites = projects.SelectMany(p => p.Suites).ToList();
                onProgress?.Invoke(suites);
                foreach (var project in projects)
                {
                    onProjectCompleted?.Invoke(project);
                }
                return Task.FromResult(new TestRunResult(suites, null, []) { Projects = projects });
            }

            return Gate is { } gate
                ? gate.Task.ContinueWith(_ => new TestRunResult([], null, []), TaskScheduler.Default)
                : Task.FromResult(new TestRunResult([], null, []));
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
                {
                    throw new TimeoutException($"Expected {count} call(s), saw {_count}.");
                }

                await Task.Delay(10);
            }

            // Let the orchestrator finish the current pipeline before the next trigger.
            await Task.Delay(50);
        }
    }
}
