// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class OrchestratorBackfillTests : IDisposable
{
    private const string Project = "/src/A.Tests/A.Tests.csproj";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("piston-backfill-").FullName;
    private readonly PistonState _state = new();
    private readonly SqliteCoverageStore _store = new();
    private readonly ClassRunner _runner = new();
    private readonly RecordingProcessor _processor = new();
    private readonly PistonOrchestrator _orchestrator;

    public OrchestratorBackfillTests()
    {
        File.WriteAllText(Path.Combine(_root, "App.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Code { }");
        _orchestrator = new PistonOrchestrator(
            new StubFileWatcherService(), new ProvenanceBuildService(), _runner, new ProvenanceImpactAnalyzer([Project]),
            _state, _store, _processor, coverageEnabled: true)
        {
            BackfillGranularity = ImpactGranularity.Class,
            BackfillDelay = TimeSpan.Zero,
        };
    }

    public void Dispose()
    {
        _orchestrator.Dispose();
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task StartAsync()
    {
        await _orchestrator.StartAsync(Path.Combine(_root, "App.slnx"));
        await WaitForAsync(() => _state.LastRun is { Trigger: TestRunTrigger.Startup });
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met.");
            }

            await Task.Delay(10, _ct);
        }
    }

    [Fact]
    public async Task WhenIdle_MeasuresEachClassAloneAndRecordsClassUnits()
    {
        await StartAsync();
        await WaitForAsync(() => _processor.Recorded.Count == 2);
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal([@"FullyQualifiedName~A.Tests.Bar.", @"FullyQualifiedName~A.Tests.Foo."], _runner.Filters.Where(f => f is not null));
        Assert.All(_processor.Recorded, r => Assert.Equal(ImpactGranularity.Class, r.Granularity));
        var units = _processor.Recorded.SelectMany(r => r.Units).ToList();
        Assert.Equal(["A.Tests.Bar", "A.Tests.Foo"], units.Select(u => u.UnitKey));
        Assert.All(units, u => Assert.False(u.Merge));
        Assert.Equal(["A.Tests.Foo.One", "A.Tests.Foo.Two"], units[1].TestFqns);
        Assert.Null(_state.ImpactBackfill);

        // Measured units are current, so the next idle period measures nothing.
        _ = await _orchestrator.RequestRunAsync();
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        Assert.Equal(2, _processor.Recorded.Count);
    }

    [Fact]
    public async Task ANewRunCancelsTheBackfill()
    {
        _runner.BlockFiltered = true;
        await StartAsync();
        await _runner.FilteredStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        Assert.NotNull(_state.ImpactBackfill);

        var run = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(TestRunStatus.Completed, run!.Status);
        Assert.True(_runner.FilteredCancelled);
        Assert.Empty(_processor.Recorded);
    }

    [Fact]
    public async Task IsOffWithoutAGranularity()
    {
        _orchestrator.BackfillGranularity = null;
        await StartAsync();
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.All(_runner.Filters, f => Assert.Null(f));
    }

    [Fact]
    public async Task GetCoveringTests_MarksEvidenceRecordedAgainstOtherContentAsNotCurrent()
    {
        await _store.InitializeAsync(_root);
        var code = Path.Combine(_root, "Code.cs");
        await _store.RecordImpactAsync(new ImpactRecording(
            Project, ImpactGranularity.Run, "run", _store.CreateRunId(), ["A.Tests.Bar.One", "A.Tests.Foo.One"],
            [new ImpactFile(code, [new LineSpan(1, 2)], "class Code\n{ int Value = 1; }")], Merge: false));
        await _store.RecordImpactAsync(new ImpactRecording(
            Project, ImpactGranularity.Class, "A.Tests.Foo", _store.CreateRunId(), ["A.Tests.Foo.One"],
            [new ImpactFile(code, [new LineSpan(1, 1)], "class Code { }")], Merge: false));

        var tests = _orchestrator.GetCoveringTests(code)!;

        Assert.Equal(["A.Tests.Bar.One", "A.Tests.Foo.One"], tests.Select(t => t.TestFqn));
        Assert.Equal(("run", false), (tests[0].Granularity, tests[0].IsCurrent));
        Assert.Equal(("class", true), (tests[1].Granularity, tests[1].IsCurrent));
        Assert.Equal([new CoveredLineRange(1, 1)], tests[1].ExecutedLines);
        Assert.Empty(_orchestrator.GetCoveringTests(Path.Combine(_root, "Other.cs"))!);
    }

    private sealed class ClassRunner : ITestRunnerService
    {
        private static readonly string[] Tests = ["A.Tests.Foo.One", "A.Tests.Foo.Two", "A.Tests.Bar.One"];

        public List<string?> Filters { get; } = [];
        public bool BlockFiltered { get; set; }
        public TaskCompletionSource FilteredStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FilteredCancelled { get; private set; }

        public Task<TestRunResult> RunTestsAsync(string solutionPath, string? filter, Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            RunTestsAsync(solutionPath, null, filter, false, onProgress, null, ct);

        public Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            RunTestsAsync(solutionPath, testProjectPaths, filter, collectCoverage, onProgress, null, ct);

        public async Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            Action<IReadOnlyList<TestSuite>>? onProgress, Action<ProjectTestResult>? onProjectCompleted, CancellationToken ct)
        {
            lock (Filters)
            {
                Filters.Add(filter);
            }

            if (filter is not null && BlockFiltered)
            {
                _ = FilteredStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    FilteredCancelled = true;
                    throw;
                }
            }

            var prefix = filter?.Replace("FullyQualifiedName~", "", StringComparison.Ordinal);
            var suite = new TestSuite("A.Tests",
                [.. Tests.Where(t => prefix is null || t.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(t => new TestResult(t, t, TestStatus.Passed, TimeSpan.Zero, null, null, null, null))],
                DateTimeOffset.UtcNow, TimeSpan.Zero);
            string[] reports = collectCoverage ? [$"/nowhere/{Guid.NewGuid():N}.cobertura.xml"] : [];
            var project = new ProjectTestResult(Project, [suite], null, reports, Crashed: false);
            onProjectCompleted?.Invoke(project);
            return new TestRunResult([suite], null, reports) { Projects = [project] };
        }

        public Task DiscoverTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter,
            Action<string, IReadOnlyList<TestSuite>> onDiscovered, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingProcessor : ICoverageProcessor
    {
        public List<CoverageImpactOptions> Recorded { get; } = [];

        public Task ProcessCoverageAsync(
            long runId, IReadOnlyList<string> coberturaXmlPaths, IReadOnlyList<string> testFqns, ICoverageStore store,
            bool isFullRun, string? sourceRoot, CoverageImpactOptions? impact) => Task.CompletedTask;

        public async Task RecordImpactAsync(long runId, CoverageImpactOptions impact, IImpactMapStore store, string? sourceRoot)
        {
            foreach (var unit in impact.Units)
            {
                await store.RecordImpactAsync(new ImpactRecording(
                    unit.ProjectPath, impact.Granularity, unit.UnitKey, runId, unit.TestFqns, [], unit.Merge));
            }

            lock (Recorded)
            {
                Recorded.Add(impact);
            }
        }
    }
}
