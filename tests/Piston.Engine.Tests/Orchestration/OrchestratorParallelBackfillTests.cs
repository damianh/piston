// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Piston.Engine.Coverage;
using Piston.Engine.Execution;
using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Piston.Engine.Tests.Coverage;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class OrchestratorParallelBackfillTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("piston-parallel-backfill-").FullName;
    private readonly PistonState _state = new();
    private readonly SqliteCoverageStore _store = new();
    private readonly StubFileWatcherService _watcher = new();
    private readonly TrackingAdmission _admission = new();
    private readonly RecordingProcessor _processor = new();
    private readonly ParallelRunner _runner;
    private readonly ProvenanceImpactAnalyzer _analyzer;
    private readonly PistonOrchestrator _orchestrator;
    private readonly string _projectA;
    private readonly string _projectB;
    private readonly string _projectC;
    private readonly string _solution;

    public OrchestratorParallelBackfillTests()
    {
        _solution = Path.Combine(_root, "App.slnx");
        _projectA = Path.Combine(_root, "A.Tests.csproj");
        _projectB = Path.Combine(_root, "B.Tests.csproj");
        _projectC = Path.Combine(_root, "C.Tests.csproj");
        File.WriteAllText(_solution, "<Solution />");
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Code { }");
        foreach (var project in new[] { _projectA, _projectB, _projectC })
        {
            _ = Directory.CreateDirectory(OutputDirectory(project));
        }

        _runner = new ParallelRunner(_root, [_projectA, _projectB, _projectC]);
        _analyzer = new ProvenanceImpactAnalyzer([_projectA, _projectB, _projectC]);
        _orchestrator = new PistonOrchestrator(
            _watcher, new ProvenanceBuildService(), _runner, _analyzer,
            _state, _store, _processor, true, NullActivityEventSink.Instance, _admission)
        {
            BackfillGranularity = ImpactGranularity.Class,
            BackfillDelay = TimeSpan.Zero,
            BackfillConcurrency = 3,
            BackfillOutputDirectory = OutputDirectory,
        };
    }

    public void Dispose()
    {
        _runner.ReleaseAll();
        _ = _runner.FinishCancellation.TrySetResult();
        _ = _admission.AllowSecondBackfill.TrySetResult();
        _orchestrator.Dispose();
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task MeasurementsRunConcurrentlyWithinTheLimit_AndPublishCompleteProgress()
    {
        _runner.UnitCounts[_projectA] = 3;
        _runner.UnitCounts[_projectB] = 3;
        _runner.UnitCounts[_projectC] = 2;
        _runner.BlockUnits = true;
        var progress = new ConcurrentQueue<ImpactBackfillProgress>();
        _state.StateChanged += () =>
        {
            if (_state.ImpactBackfill is { } current)
            {
                progress.Enqueue(current);
            }
        };

        await StartAsync();
        await WaitForAsync(() => _runner.ActiveCount == 3 && _runner.Started.Count == 3);
        Assert.Equal(3, _runner.Started.Count);
        Assert.Equal(1, _admission.ActiveBackfill);
        Assert.Empty(_processor.Recorded);

        _runner.BlockUnits = false;
        _runner.ReleaseAll();
        await WaitForAsync(() => _processor.Recorded.Count == 8);
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(15), _ct);

        Assert.Equal(3, _runner.MaxActive);
        Assert.Equal(3, _store.GetUnits(_projectA, ImpactGranularity.Class).Count);
        Assert.Equal(3, _store.GetUnits(_projectB, ImpactGranularity.Class).Count);
        Assert.Equal(2, _store.GetUnits(_projectC, ImpactGranularity.Class).Count);
        Assert.Equal(1, _runner.MaxActiveFor(_projectA));
        Assert.Equal(1, _runner.MaxActiveFor(_projectB));
        Assert.Equal(1, _runner.MaxActiveFor(_projectC));
        Assert.All(_runner.Started, unit => Assert.True(unit.Callbacks.ReuseDiscovery));
        Assert.All(progress, item => Assert.InRange(item.Completed, 0, item.Total));
        Assert.Contains(progress, item => item.Completed == 8 && item.Total == 8);
        Assert.Null(_state.ImpactBackfill);
        Assert.Equal(0, _admission.ActiveBackfill);
    }

    [Fact]
    public async Task CancellationDrainsAllMeasurements_BeforeReleasingAdmission()
    {
        _runner.UnitCounts[_projectA] = 3;
        _runner.UnitCounts[_projectB] = 3;
        _runner.UnitCounts[_projectC] = 2;
        _runner.BlockUnits = true;
        _runner.BlockCancellation = true;
        await StartAsync();
        await WaitForAsync(() => _runner.ActiveCount == 3 && _runner.Started.Count == 3);

        _orchestrator.BackfillGranularity = null;
        var foreground = _orchestrator.RequestRunAsync();
        await WaitForAsync(() => _runner.CancelledCount == 3);
        Assert.False(foreground.IsCompleted);
        Assert.Equal(1, _admission.ActiveBackfill);
        Assert.Equal(3, _runner.Started.Count);

        _runner.FinishCancellation.SetResult();
        var result = await foreground.WaitAsync(TimeSpan.FromSeconds(15), _ct);
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(15), _ct);

        Assert.Equal(TestRunStatus.Completed, result!.Status);
        Assert.Equal(0, _admission.ActiveBackfill);
        Assert.Empty(_processor.Recorded);
        Assert.Empty(_state.ProjectResourceWaits);
    }

    [Fact]
    public async Task MeasurementsForTheSameProjectAreSerialized_WithoutBlockingAnotherProject()
    {
        _runner.UnitCounts[_projectA] = 3;
        _runner.UnitCounts[_projectB] = 3;
        _runner.BlockUnits = true;
        _orchestrator.BackfillConcurrency = 2;

        await StartAsync();
        await WaitForAsync(() => _runner.Started.Any(unit => unit.Project == _projectA)
            && _runner.Started.Any(unit => unit.Project == _projectB));
        Assert.Equal(1, _runner.ActiveFor(_projectA));
        Assert.Equal(1, _runner.ActiveFor(_projectB));

        _runner.BlockUnits = false;
        _runner.ReleaseAll();
        await WaitForAsync(() => _processor.Recorded.Count == 6);
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(15), _ct);

        Assert.Equal(1, _runner.MaxActiveFor(_projectA));
        Assert.Equal(2, _runner.MaxActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectsWithOverlappingOutputsAreSerialized_WithoutBlockingAnotherOutput(bool nested)
    {
        _runner.UnitCounts[_projectA] = 2;
        _runner.UnitCounts[_projectB] = 2;
        _runner.UnitCounts[_projectC] = 2;
        _runner.BlockUnits = true;
        var shared = Directory.CreateDirectory(Path.Combine(_root, "shared-output")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_root, "shared-output-other")).FullName;
        var alias = nested
            ? Directory.CreateDirectory(Path.Combine(shared, "nested")).FullName
            : OperatingSystem.IsWindows()
                ? shared
                : Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), shared).FullName;
        _orchestrator.BackfillOutputDirectory = project => project == _projectC ? other : project == _projectB ? alias : shared;

        await StartAsync();
        await WaitForAsync(() => _runner.Started.Count == 2);
        Assert.Equal(1, _runner.ActiveFor(_projectA) + _runner.ActiveFor(_projectB));
        Assert.Equal(1, _runner.ActiveFor(_projectC));

        _runner.BlockUnits = false;
        _runner.ReleaseAll();
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(15), _ct);
        Assert.Equal(6, _processor.Recorded.Count);
        Assert.Equal(2, _runner.MaxActive);
        Assert.All(_runner.ActiveSnapshots, active =>
            Assert.InRange(active.GetValueOrDefault(_projectA) + active.GetValueOrDefault(_projectB), 0, 1));
    }

    [Fact]
    public async Task UnknownOutputForcesASerialPass()
    {
        _runner.UnitCounts[_projectA] = 2;
        _runner.UnitCounts[_projectB] = 2;
        _runner.BlockUnits = true;
        _orchestrator.BackfillOutputDirectory = project => project == _projectB ? null : project;

        await StartAsync();
        await WaitForAsync(() => _runner.Started.Count == 1);
        Assert.Equal(1, _runner.ActiveCount);

        _runner.BlockUnits = false;
        _runner.ReleaseAll();
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(15), _ct);
        Assert.Equal(4, _processor.Recorded.Count);
        Assert.Equal(1, _runner.MaxActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedOutputFilesForceASerialPass(bool hardLink)
    {
        _runner.UnitCounts[_projectA] = 2;
        _runner.UnitCounts[_projectB] = 2;
        _runner.BlockUnits = true;
        var target = Path.Combine(OutputDirectory(_projectA), "Target.dll");
        File.WriteAllText(target, "binary");
        var link = Path.Combine(OutputDirectory(_projectB), "Target.dll");
        if (hardLink)
        {
            CoverageOutputIsolationTests.CreateHardLinkForTest(link, target);
        }
        else
        {
            try
            {
                _ = File.CreateSymbolicLink(link, target);
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                Assert.Skip("Creating symbolic links is not permitted on this Windows machine.");
            }
        }

        await StartAsync();
        await WaitForAsync(() => _runner.Started.Count >= 1);
        await Task.Delay(100, _ct);
        _ = Assert.Single(_runner.Started);
        _runner.BlockUnits = false;
        _runner.ReleaseAll();
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(15), _ct);

        Assert.Equal(4, _processor.Recorded.Count);
        Assert.Equal(1, _runner.MaxActive);
    }

    [Fact]
    public async Task FinishingOneMeasurement_DoesNotEraseAnotherMeasurementsResourceWait()
    {
        _runner.UnitCounts[_projectA] = 1;
        _runner.UnitCounts[_projectB] = 1;
        _runner.BlockUnits = true;
        _orchestrator.BackfillConcurrency = 2;
        await StartAsync();
        await WaitForAsync(() => _runner.Started.Count == 2);
        var units = _runner.Started.ToArray();
        var waiting = units.Single(unit => unit.Project == _projectA);
        var running = units.Single(unit => unit.Project == _projectB);
        var now = DateTimeOffset.UtcNow;
        waiting.Callbacks.OnProjectWaiting!(_projectA, new TestResourceWait(
            TestResourceWaitState.Waiting, ["port:5432"], false, [], 0, 0, null, now, null));
        Assert.Equal(ProjectRunStatus.WaitingForResources, _state.ProjectStatuses[_projectA]);

        running.Release.SetResult();
        await WaitForAsync(() => _runner.Completed.Contains(running.Filter));
        Assert.Equal(ProjectRunStatus.WaitingForResources, _state.ProjectStatuses[_projectA]);
        Assert.True(_state.ProjectResourceWaits.ContainsKey(_projectA));

        waiting.Callbacks.OnProjectStarted!(_projectA);
        Assert.Equal(ProjectRunStatus.Running, _state.ProjectStatuses[_projectA]);
        Assert.Empty(_state.ProjectResourceWaits);
        waiting.Release.SetResult();
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(15), _ct);

        Assert.Equal(ProjectRunStatus.Completed, _state.ProjectStatuses[_projectA]);
        Assert.Empty(_state.ProjectResourceWaits);
    }

    [Fact]
    public async Task ProgressShowsAMeasurementThatIsStillRunning()
    {
        _runner.UnitCounts[_projectA] = 1;
        _runner.UnitCounts[_projectB] = 1;
        _runner.BlockUnits = true;
        _orchestrator.BackfillConcurrency = 2;
        await StartAsync();
        await WaitForAsync(() => _runner.Started.Count == 2);
        var units = _runner.Started.ToArray();
        var displayed = units.Single(unit => unit.Project == _state.ImpactBackfill!.ProjectPath);
        var other = units.Single(unit => unit.Project != displayed.Project);

        displayed.Release.SetResult();
        await WaitForAsync(() => _state.ImpactBackfill is { Completed: 1 });
        Assert.Equal(other.Project, _state.ImpactBackfill!.ProjectPath);

        other.Release.SetResult();
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(15), _ct);
    }

    [Fact]
    public async Task AdmissionIsReleasedBetweenChunks()
    {
        _runner.UnitCounts[_projectA] = 3;
        _runner.UnitCounts[_projectB] = 2;
        _orchestrator.BackfillConcurrency = 2;
        _admission.BlockSecondBackfill = true;

        await StartAsync();
        await _admission.SecondBackfillStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), _ct);
        Assert.Equal(1, _admission.ReleasedBackfill);
        Assert.Equal(0, _admission.ActiveBackfill);
        Assert.Equal(4, _processor.Recorded.Count);
        _admission.AllowSecondBackfill.SetResult();
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(15), _ct);

        Assert.Equal(2, _admission.AdmittedBackfill);
        Assert.Equal(2, _admission.ReleasedBackfill);
        Assert.Equal(5, _processor.Recorded.Count);
    }

    [Fact]
    public async Task FailedMeasurementsAreNotRetriedOnTheNextPass()
    {
        _runner.UnitCounts[_projectA] = 6;
        _runner.FailedFilters.UnionWith([
            "FullyQualifiedName~A.Tests.C00.",
            "FullyQualifiedName~A.Tests.C01.",
        ]);
        await StartAsync();
        await WaitForAsync(() => _admission.ReleasedBackfill > 0);
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(15), _ct);
        Assert.Equal(4, _processor.Recorded.Count);

        _ = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(15), _ct);
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(15), _ct);

        Assert.Equal(6, _runner.Started.Count);
        Assert.Equal(4, _processor.Recorded.Count);
    }

    [Fact]
    public async Task SourceChangeCancelsInFlightMeasurements_WithoutStartingQueuedUnits()
    {
        _runner.UnitCounts[_projectA] = 3;
        _runner.UnitCounts[_projectB] = 3;
        _runner.UnitCounts[_projectC] = 2;
        _runner.BlockUnits = true;
        await StartAsync();
        await WaitForAsync(() => _runner.ActiveCount == 3 && _runner.Started.Count == 3);
        _orchestrator.BackfillGranularity = null;
        var now = DateTimeOffset.UtcNow;

        _watcher.TriggerChange(new FileChangeBatch(
            [new FileChangeEvent(Path.Combine(_root, "Code.cs"), WatcherChangeTypes.Changed, now)], now));
        await WaitForAsync(() => _runner.CancelledCount == 3);
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(15), _ct);

        Assert.Equal(3, _runner.Started.Count);
        Assert.Empty(_processor.Recorded);
    }

    private async Task StartAsync()
    {
        _analyzer.TestProjects = [.. _runner.UnitCounts.Where(item => item.Value > 0).Select(item => item.Key)];
        await _orchestrator.StartAsync(_solution);
        await WaitForAsync(() => _state.LastRun is { Trigger: TestRunTrigger.Startup });
    }

    private string OutputDirectory(string project) => Path.Combine(_root, "output", Path.GetFileNameWithoutExtension(project));

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline,
                $"Condition was not met. Run={_state.LastRun?.Status}/{_state.LastRun?.Freshness}; " +
                $"backfill={_orchestrator.BackfillTask.Status}; started={_runner.Started.Count}; active={_runner.ActiveCount}; " +
                $"admissions={_admission.AdmittedBackfill}; recorded={_processor.Recorded.Count}; " +
                $"suites={string.Join(", ", _state.TestSuites.Select(suite => $"{suite.Name}/{suite.ProjectId}/{suite.Tests.Count}"))}");
            await Task.Delay(10, _ct);
        }
    }

    private sealed class ParallelRunner(string solutionDirectory, IReadOnlyList<string> projects) : ITestRunnerService
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, int> _activeByProject = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _maxByProject = new(StringComparer.Ordinal);
        private int _active;
        private int _maxActive;
        private int _cancelled;

        public Dictionary<string, int> UnitCounts { get; } = projects.ToDictionary(project => project, _ => 0, StringComparer.Ordinal);
        public HashSet<string> FailedFilters { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<UnitControl> Started { get; } = new();
        public ConcurrentQueue<string> Completed { get; } = new();
        public ConcurrentQueue<IReadOnlyDictionary<string, int>> ActiveSnapshots { get; } = new();
        public TaskCompletionSource FinishCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool BlockUnits;
        public bool BlockCancellation { get; set; }
        public int CancelledCount => Volatile.Read(ref _cancelled);

        public int ActiveCount { get { lock (_gate) { return _active; } } }
        public int MaxActive { get { lock (_gate) { return _maxActive; } } }
        public int ActiveFor(string project) { lock (_gate) { return _activeByProject.GetValueOrDefault(project); } }
        public int MaxActiveFor(string project) { lock (_gate) { return _maxByProject.GetValueOrDefault(project); } }

        public void ReleaseAll()
        {
            foreach (var unit in Started)
            {
                _ = unit.Release.TrySetResult();
            }
        }

        public Task<TestRunResult> RunTestsAsync(
            string solutionPath, string? filter, Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            RunTestsAsync(solutionPath, null, filter, false, onProgress, null, ct);

        public Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            RunTestsAsync(solutionPath, testProjectPaths, filter, collectCoverage, onProgress, null, ct);

        public Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            Action<IReadOnlyList<TestSuite>>? onProgress, Action<ProjectTestResult>? onProjectCompleted, CancellationToken ct) =>
            RunTestsAsync(solutionPath, testProjectPaths, filter, collectCoverage,
                new TestRunCallbacks { OnProjectCompleted = onProjectCompleted }, ct);

        public async Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            TestRunCallbacks callbacks, CancellationToken ct)
        {
            if (filter is null)
            {
                var results = (testProjectPaths ?? projects).Select(project => Result(project, null, collectCoverage: false)).ToList();
                foreach (var result in results)
                {
                    callbacks.OnProjectStarted?.Invoke(result.ProjectPath);
                    callbacks.OnProjectCompleted?.Invoke(result);
                }

                return new TestRunResult([.. results.SelectMany(result => result.Suites)], null, []) { Projects = results };
            }

            var project = Assert.Single(testProjectPaths!);
            var control = new UnitControl(project, filter, callbacks);
            lock (_gate)
            {
                _active++;
                _maxActive = Math.Max(_maxActive, _active);
                _activeByProject[project] = _activeByProject.GetValueOrDefault(project) + 1;
                _maxByProject[project] = Math.Max(_maxByProject.GetValueOrDefault(project), _activeByProject[project]);
                ActiveSnapshots.Enqueue(new Dictionary<string, int>(_activeByProject, StringComparer.Ordinal));
            }

            try
            {
                callbacks.OnProjectResourcesAcquired?.Invoke(project);
                callbacks.OnProjectStarted?.Invoke(project);
                Started.Enqueue(control);
                if (BlockUnits)
                {
                    try
                    {
                        await control.Release.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        _ = Interlocked.Increment(ref _cancelled);
                        if (BlockCancellation)
                        {
                            await FinishCancellation.Task;
                        }

                        throw;
                    }
                }

                var result = Result(project, filter, collectCoverage);
                callbacks.OnProjectCompleted?.Invoke(result);
                Completed.Enqueue(filter);
                return new TestRunResult(result.Suites, result.RunnerError, result.CoverageReportPaths) { Projects = [result] };
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                    _activeByProject[project]--;
                }
            }
        }

        private ProjectTestResult Result(string project, string? filter, bool collectCoverage)
        {
            var name = TestProjectNames.SuiteName(project);
            var prefix = filter?.Replace("FullyQualifiedName~", "", StringComparison.Ordinal);
            var tests = Enumerable.Range(0, UnitCounts[project])
                .Select(index => $"{name}.C{index:00}.Test")
                .Where(test => prefix is null || test.StartsWith(prefix, StringComparison.Ordinal))
                .Select(test => new TestResult(test, test, TestStatus.Passed, TimeSpan.Zero, null, null, null, null))
                .ToList();
            var suite = new TestSuite(name, tests, DateTimeOffset.UtcNow, TimeSpan.Zero)
            {
                ProjectId = TestProjectNames.ProjectId(solutionDirectory, project),
            };
            var failed = filter is not null && FailedFilters.Contains(filter);
            string[] reports = collectCoverage && !failed ? [$"/nowhere/{Guid.NewGuid():N}.cobertura.xml"] : [];
            return new ProjectTestResult(project, [suite], failed ? "failed" : null, reports, Crashed: failed);
        }
    }

    private sealed record UnitControl(string Project, string Filter, TestRunCallbacks Callbacks)
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecordingProcessor : ICoverageProcessor
    {
        public ConcurrentQueue<CoverageImpactOptions> Recorded { get; } = new();

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

            Recorded.Enqueue(impact);
        }
    }

    private sealed class TrackingAdmission : IExecutionAdmission
    {
        private int _admitted;
        private int _released;
        private int _active;
        public bool BlockSecondBackfill { get; set; }
        public TaskCompletionSource SecondBackfillStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSecondBackfill { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AdmittedBackfill => Volatile.Read(ref _admitted);
        public int ReleasedBackfill => Volatile.Read(ref _released);
        public int ActiveBackfill => Volatile.Read(ref _active);

        public async Task<IExecutionLease> AdmitAsync(
            string runId, Action<TestRunAdmission> onStatus, long? inheritedSequence, CancellationToken ct)
        {
            var backfill = runId.StartsWith("backfill-", StringComparison.Ordinal);
            if (backfill)
            {
                var admission = Interlocked.Increment(ref _admitted);
                if (admission == 2 && BlockSecondBackfill)
                {
                    SecondBackfillStarted.SetResult();
                    await AllowSecondBackfill.Task.WaitAsync(ct);
                }

            }

            ct.ThrowIfCancellationRequested();
            if (backfill)
            {
                _ = Interlocked.Increment(ref _active);
            }

            return new Lease(() =>
            {
                if (backfill)
                {
                    _ = Interlocked.Decrement(ref _active);
                    _ = Interlocked.Increment(ref _released);
                }
            });
        }

        private sealed class Lease(Action release) : IExecutionLease
        {
            private int _released;
            public TestRunAdmission? Status => null;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    release();
                }
            }
        }
    }
}
