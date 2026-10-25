// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Engine.Execution;
using Piston.Engine.History;
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
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
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

    [Theory]
    [InlineData("test")]
    [InlineData("blocker")]
    [InlineData("empty-solution")]
    [InlineData("empty-project")]
    [InlineData("invalid-solution")]
    [InlineData("invalid-project")]
    public async Task Structurally_invalid_saved_results_do_not_reach_startup_notifications(string corruption)
    {
        var savedSuite = new TestSuite("A.Tests",
            [new TestResult("A.Tests.Test1", "Test1", TestStatus.Passed, TimeSpan.Zero, null, null, null, null)],
            DateTimeOffset.UtcNow, TimeSpan.Zero);
        var savedRun = new TestRunRecord
        {
            RunId = "saved-run",
            EngineInstanceId = "saved-engine",
            Sequence = 1,
            Trigger = TestRunTrigger.Startup,
            RequestedAt = DateTimeOffset.UtcNow,
            Context = new TestRunContext(
                SolutionPath, [Projects[0]], null, null, null, null, false,
                "Debug", null, ".NET", "OS", _root),
            Suites = [savedSuite],
            Projects =
            [
                new(Projects[0], ProjectRunStatus.Completed, 1)
                {
                    Resources = new(TestResourceNeeds.None(Projects[0]),
                        new TestResourceWait(TestResourceWaitState.Acquired, [], false,
                            [new("database", TestResourceBlockerKind.Held, 1, null, null, DateTimeOffset.UtcNow)],
                            0, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), []),
                },
            ],
        };
        using (var writer = new SavedResultsStore(SolutionPath, _ => { }))
        {
            writer.Schedule(new SavedResultsSnapshot(
                DateTimeOffset.UtcNow, null, null, null, null, [savedSuite], savedRun));
        }

        var filePath = Path.Combine(_root, ".piston", "App.results.json");
        var json = JsonNode.Parse(File.ReadAllText(filePath))!;
        if (corruption == "blocker")
        {
            json["LastRun"]!["Projects"]![0]!["Resources"]!["Wait"]!["BlockedBy"]![0] = null;
        }
        else if (corruption == "test")
        {
            json["TestSuites"]![0]!["Tests"]![0] = null;
        }
        else if (corruption.EndsWith("solution", StringComparison.Ordinal))
        {
            json["LastRun"]!["Context"]!["SolutionPath"] = corruption == "empty-solution" ? "" : "\0";
        }
        else
        {
            json["LastRun"]!["Context"]!["TestProjects"]![0] = corruption == "empty-project" ? "" : "\0";
        }
        File.WriteAllText(filePath, json.ToJsonString());
        using var store = new SavedResultsStore(SolutionPath, _ => { });
        _orchestrator.ResultsStore = store;
        _state.StateChanged += () =>
        {
            Assert.False(_state.LastRun?.Restored ?? false);
            foreach (var result in _state.TestSuites.SelectMany(s => s.Tests))
            {
                _ = result.FullyQualifiedName.Length;
            }
            foreach (var project in _state.LastRun?.Projects ?? [])
            {
                foreach (var blocker in project.Resources?.Wait?.BlockedBy ?? [])
                {
                    _ = blocker.Resource.Length;
                }
            }
        };

        await StartAndWaitForStartupRunAsync();

        Assert.Equal(TestRunStatus.Completed, _state.LastRun!.Status);
        Assert.DoesNotContain(_state.TestSuites, suite => suite.Restored);
    }

    [Fact]
    public async Task ClearResults_waits_for_snapshot_capture_and_discards_the_pending_save()
    {
        await StartAndWaitForStartupRunAsync();
        using var store = new SavedResultsStore(SolutionPath, _ => { })
        {
            SaveDelay = Timeout.InfiniteTimeSpan,
        };
        _orchestrator.ResultsStore = store;
        var capturing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCapture = new ManualResetEventSlim();
        var runRecordsLock = typeof(PistonOrchestrator)
            .GetField("_runRecordsLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_orchestrator)!;
        _state.TestSuites = new SnapshotCaptureSuites(_state.TestSuites, () =>
        {
            capturing.SetResult();
            Assert.True(Monitor.IsEntered(runRecordsLock), "Snapshot capture must share ClearResults' run-record lock.");
            Assert.True(releaseCapture.Wait(TimeSpan.FromSeconds(10), _ct));
        });
        _build.Fail = true;
        var completion = Task.Run(() => _orchestrator.RequestRunAsync(), _ct);
        Task? clear = null;
        try
        {
            await capturing.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
            var clearing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            clear = Task.Run(() =>
            {
                clearing.SetResult();
                _orchestrator.ClearResults(_root);
            }, _ct);
            await clearing.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);

            Assert.NotSame(clear, await Task.WhenAny(clear, Task.Delay(TimeSpan.FromMilliseconds(200), _ct)));
        }
        finally
        {
            releaseCapture.Set();
            _ = await completion.WaitAsync(TimeSpan.FromSeconds(10), _ct);
            if (clear is not null)
            {
                await clear.WaitAsync(TimeSpan.FromSeconds(10), _ct);
            }
        }

        store.Dispose();
        Assert.Empty(_state.TestSuites);
        Assert.Null(store.TryLoad());
    }

    private sealed class SnapshotCaptureSuites(IReadOnlyList<TestSuite> suites, Action onEnumerate) : IReadOnlyList<TestSuite>
    {
        public int Count => suites.Count;
        public TestSuite this[int index] => suites[index];

        public IEnumerator<TestSuite> GetEnumerator()
        {
            onEnumerate();
            return suites.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task Selective_run_after_failed_startup_does_not_count_restored_tests_as_verified()
    {
        using (var writer = new SavedResultsStore(SolutionPath, _ => { }))
        {
            var suites = Projects.Select(project =>
            {
                var name = Path.GetFileNameWithoutExtension(project);
                return new TestSuite(name,
                    [new TestResult($"{name}.Test1", "Test1", TestStatus.Passed, TimeSpan.Zero, null, null, null, null)],
                    DateTimeOffset.UtcNow, TimeSpan.Zero)
                {
                    ProjectId = TestProjectNames.ProjectId(_root, project),
                };
            }).ToList();
            writer.Schedule(new SavedResultsSnapshot(
                DateTimeOffset.UtcNow, null, null, null, null, suites, null));
        }

        using var store = new SavedResultsStore(SolutionPath, _ => { });
        _orchestrator.ResultsStore = store;
        _build.Fail = true;
        await StartAndWaitForStartupRunAsync();

        Assert.Equal(TestRunStatus.BuildFailed, _state.LastRun!.Status);
        Assert.All(_state.TestSuites, suite => Assert.True(suite.Restored));
        Assert.Equal(0, _state.VerifiedSinceChangeCount);
        Assert.Null(_state.LastFileChangeTime);

        _build.Fail = false;
        var run = await _orchestrator.RunSelectedAsync(new TestSelection([TestProjectNames.ProjectId(_root, Projects[0])]));

        Assert.Equal(TestRunStatus.Completed, run.Status);
        Assert.False(Assert.Single(_state.TestSuites, suite => suite.Name == "A.Tests").Restored);
        Assert.True(Assert.Single(_state.TestSuites, suite => suite.Name == "B.Tests").Restored);
        Assert.Equal(1, _state.VerifiedSinceChangeCount);
        Assert.Null(_state.LastFileChangeTime);
    }

    [Fact]
    public async Task Restored_results_are_unknown_until_startup_run_replaces_them()
    {
        var savedStore = new SavedResultsStore(SolutionPath, _ => { });
        savedStore.Schedule(new SavedResultsSnapshot(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(2),
            new BuildResult(BuildStatus.Succeeded, [], [], TimeSpan.FromMilliseconds(1)),
            [new TestSuite("A.Tests",
                [new TestResult("A.Tests.Test1", "Test1", TestStatus.Failed, TimeSpan.FromMilliseconds(2), null, "failed before restart", null, null)],
                DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(2))
            {
                ProjectId = TestProjectNames.ProjectId(_root, Projects[0]),
            }],
            new TestRunRecord
            {
                RunId = "previous-run",
                EngineInstanceId = "previous-engine",
                Sequence = 7,
                Trigger = TestRunTrigger.Startup,
                Status = TestRunStatus.Completed,
                Freshness = ResultFreshness.Current,
                RequestedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                Suites = [new TestSuite("A.Tests",
                    [new TestResult("A.Tests.Test1", "Test1", TestStatus.Failed, TimeSpan.FromMilliseconds(2), null, "failed before restart", null, null)],
                    DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(2))
                {
                    ProjectId = TestProjectNames.ProjectId(_root, Projects[0]),
                }],
            }));
        savedStore.Dispose();

        using var store = new SavedResultsStore(SolutionPath, _ => { });
        _orchestrator.ResultsStore = store;
        var releaseGraph = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _impact.Initialization = releaseGraph.Task;
        try
        {
            await _orchestrator.StartAsync(SolutionPath);

            Assert.True(_state.LastRun!.Restored);
            Assert.Equal(0, _state.LastRun.Sequence);
            Assert.Equal(ResultFreshness.Unknown, _state.LastRun.Freshness);
            Assert.False(_state.LastRun.IsCurrentFullResult);
            Assert.True(Assert.Single(_state.TestSuites).Restored);
            Assert.Equal("previous-run", _state.FindRun("previous-run")!.RunId);

            _ = releaseGraph.TrySetResult();
            await WaitForAsync(() => _state.LastRun is { RunId: not "previous-run", Status: TestRunStatus.Completed });

            Assert.False(_state.LastRun!.Restored);
            Assert.Equal(ResultFreshness.Current, _state.LastRun.Freshness);
            Assert.Equal(1, _state.LastRun.Sequence);
            Assert.Contains(_state.LastRun.FixedTests, result => result.Key == "A.Tests.Test1");
            Assert.DoesNotContain(_state.TestSuites, suite => suite.Restored);
        }
        finally
        {
            _ = releaseGraph.TrySetResult();
        }
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
    public async Task Runs_RecordTheirTriggeringFilesAndTheTestsTheyFixedOrBroke()
    {
        await StartAndWaitForStartupRunAsync();
        var code = Path.Combine(_root, "Code.cs");

        _runner.FailingTest = "A.Tests.Test1";
        var broken = await _orchestrator.RequestRunAsync();
        _runner.FailingTest = null;
        _watcher.TriggerChange(new FileChangeBatch(
            [new FileChangeEvent(code, WatcherChangeTypes.Changed, DateTimeOffset.UtcNow),
             new FileChangeEvent(code, WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)], DateTimeOffset.UtcNow));
        await WaitForAsync(() => _state.LastRun is { Trigger: TestRunTrigger.FileChange });
        var fixedRun = _state.LastRun!;

        Assert.Empty(broken!.ChangedFiles);
        Assert.Equal([new("A.Tests", "A.Tests.Test1") { ProjectId = TestProjectNames.ProjectId(_root, Projects[0]) }], broken.NewlyFailingTests);
        Assert.Empty(broken.FixedTests);
        Assert.Equal([code], fixedRun.ChangedFiles);
        Assert.Equal([new("A.Tests", "A.Tests.Test1") { ProjectId = TestProjectNames.ProjectId(_root, Projects[0]) }], fixedRun.FixedTests);
        Assert.Empty(fixedRun.NewlyFailingTests);
    }

    [Fact]
    public async Task StartupRun_RunsTestProjectsFoundOnlyOnceTheBuildRestoredTheirPackages()
    {
        // A fresh clone: xUnit v3 / TUnit projects are recognized through properties their packages set.
        _impact.TestProjects = [Projects[0]];
        _impact.TestProjectsAfterRestore = Projects;

        await StartAndWaitForStartupRunAsync();

        var run = _state.LastRun!;
        Assert.Equal(Projects, run.Context!.TestProjects);
        Assert.Equal(Projects, run.Context.TestWorkingDirectories!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["A.Tests", "B.Tests"], _state.TestSuites.Select(s => s.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupHistoryMigrationWaitsForTheCompletePostRestoreGraph(bool ambiguous)
    {
        var restoredProject = ambiguous ? "/src/Other/A.Tests.csproj" : Projects[1];
        var legacyName = Path.GetFileNameWithoutExtension(restoredProject);
        var historyDirectory = _root + "-history";
        WriteLegacyHistory(historyDirectory, legacyName);
        using var history = RunHistoryStore.Open(historyDirectory, SolutionPath);
        _orchestrator.RunHistory = history;
        _impact.TestProjects = [Projects[0]];
        _impact.TestProjectsAfterRestore = [Projects[0], restoredProject];
        _build.DuringBuild = () =>
            Assert.Equal(legacyName, Assert.Single(history.GetTestHistory("Shared.Test")).ProjectId);

        try
        {
            await StartAndWaitForStartupRunAsync();

            var migrated = history.GetTestHistory("Shared.Test");
            if (ambiguous)
            {
                Assert.Empty(migrated);
            }
            else
            {
                Assert.Equal(TestProjectNames.ProjectId(_root, restoredProject), Assert.Single(migrated).ProjectId);
            }
        }
        finally
        {
            _orchestrator.RunHistory = null;
            history.Dispose();
            Directory.Delete(historyDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteGraphOrFailedBuildLeavesLegacyHistoryUnchanged(bool buildFails)
    {
        var historyDirectory = _root + "-history";
        WriteLegacyHistory(historyDirectory, "A.Tests");
        var location = HistoryLocation.Resolve(historyDirectory, SolutionPath);
        var original = File.ReadAllText(location.FilePath);
        var messages = new List<string>();
        using var history = RunHistoryStore.Open(historyDirectory, SolutionPath, messages.Add);
        _orchestrator.RunHistory = history;
        _impact.ProjectDiscoveryComplete = false;
        _build.Fail = buildFails;

        try
        {
            await StartAndWaitForStartupRunAsync();
            history.Save();

            Assert.Equal(original, File.ReadAllText(location.FilePath));
            Assert.Equal("A.Tests", Assert.Single(history.GetTestHistory("Shared.Test")).ProjectId);
            if (!buildFails)
            {
                Assert.Contains(messages, m => m.Contains("waiting for complete project discovery", StringComparison.Ordinal));
            }

            _impact.ProjectDiscoveryComplete = true;
            _build.Fail = false;
            _ = await _orchestrator.RequestRunAsync();
            Assert.Equal(TestProjectNames.ProjectId(_root, Projects[0]),
                Assert.Single(history.GetTestHistory("Shared.Test")).ProjectId);
        }
        finally
        {
            _orchestrator.RunHistory = null;
            history.Dispose();
            Directory.Delete(historyDirectory, recursive: true);
        }
    }

    private void WriteLegacyHistory(string directory, string project)
    {
        var location = HistoryLocation.Resolve(directory, SolutionPath);
        _ = Directory.CreateDirectory(location.Directory);
        File.WriteAllText(location.FilePath, JsonSerializer.Serialize(new
        {
            Version = 1,
            Names = new[] { project, "Shared.Test" },
            Tests = new[]
            {
                new
                {
                    Project = 0,
                    Test = 1,
                    Runs = new[] { new { RunId = "legacy-run", At = DateTimeOffset.UtcNow, Status = "P", DurationMs = 1 } },
                },
            },
            Runs = Array.Empty<object>(),
        }));
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
    public async Task FailureAfterAPassOnEditedSource_IsClassifiedNew_AndOnUnchangedSource_Flaky()
    {
        // History lives outside the checkout, as it does in production; its lock file would otherwise block fingerprinting.
        var historyDirectory = _root + "-history";
        using var history = RunHistoryStore.Open(historyDirectory, SolutionPath);
        _orchestrator.RunHistory = history;
        await StartAndWaitForStartupRunAsync();
        Assert.Empty(_state.LastRun!.FailureClassifications);

        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Code { int X; }");
        _runner.FailingTest = "B.Tests.Test1";
        var broken = await _orchestrator.RequestRunAsync();
        _runner.FailingTest = null;
        _ = await _orchestrator.RequestRunAsync();
        _runner.FailingTest = "B.Tests.Test1";
        var flaky = await _orchestrator.RequestRunAsync();

        var classification = Assert.Single(broken!.FailureClassifications);
        Assert.Equal(new FailureClassification("B.Tests", "B.Tests.Test1", FailureKind.New, classification.Evidence)
        {
            ProjectId = TestProjectNames.ProjectId(_root, Projects[1]),
        }, classification);
        Assert.Equal(FailureKind.Flaky, Assert.Single(flaky!.FailureClassifications).Kind);
        Assert.Equal(4, history.GetRuns().Count);
        _orchestrator.RunHistory = null;
        history.Dispose();
        Directory.Delete(historyDirectory, recursive: true);
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
    public async Task IdenticalTestsInDifferentProjects_AreNotDuplicateIdentities()
    {
        _impact.TestProjects =
        [
            Path.Combine(_root, "One", "Tests.csproj"),
            Path.Combine(_root, "Two", "Tests.csproj"),
        ];
        await StartAndWaitForStartupRunAsync();

        var run = await _orchestrator.RequestRunAsync();

        Assert.Equal(TestRunStatus.Completed, run!.Status);
        Assert.Equal(["One/Tests.csproj", "Two/Tests.csproj"], run.Suites.Select(s => s.ProjectId));
        Assert.All(run.Tests, test => Assert.Equal("Tests.Test1", test.ResultKey));
        Assert.Empty(run.DuplicateTestIds);
        Assert.True(run.IsCurrentFullResult);

        _runner.DuplicateFirstTest = true;
        var duplicated = await _orchestrator.RequestRunAsync();

        Assert.Equal(["Tests.Test1"], duplicated!.DuplicateTestIds);
        Assert.False(duplicated.IsCurrentFullResult);
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
    public async Task OlderRecentRunsDropExecutionLogs_WhileLatestAggregateKeepsItsLog()
    {
        _runner.AttachExecutionLog = true;
        await StartAndWaitForStartupRunAsync();
        var older = _state.LastRun!;
        Assert.NotNull(older.Suites.First(s => s.Name == "A.Tests").ExecutionLog);

        var newer = await _orchestrator.RequestRunAsync();

        Assert.NotNull(newer!.Suites.First(s => s.Name == "A.Tests").ExecutionLog);
        Assert.Null(_state.FindRun(older.RunId)!.Suites.First(s => s.Name == "A.Tests").ExecutionLog);
        var aggregate = _state.TestSuites.First(s => s.Name == "A.Tests");
        Assert.NotNull(aggregate.ExecutionLog);
        Assert.Equal(newer.RunId, aggregate.RunId);
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
    public IReadOnlyList<string> TestProjects { get; set; } = testProjects;

    /// <summary>The test projects the graph finds once reloaded after restore; null when it was restored already.</summary>
    public IReadOnlyList<string>? TestProjectsAfterRestore { get; set; }
    public bool ProjectDiscoveryComplete { get; set; } = true;

    public IReadOnlyList<string>? GetRestoredTestProjectPaths() =>
        ProjectDiscoveryComplete && TestProjectsAfterRestore is null ? TestProjects : null;

    public bool RefreshGraphIfEvaluatedBeforeRestore()
    {
        if (TestProjectsAfterRestore is not { } restored)
        {
            return false;
        }

        TestProjects = restored;
        TestProjectsAfterRestore = null;
        return true;
    }

    public Func<string?> VerificationCoverage { get; set; } = () => null;
    public string? GetVerificationCoverageIssue(string solutionPath) => VerificationCoverage();
    public Task Initialization { get; set; } = Task.CompletedTask;
    public HashSet<string> MtpProjects { get; } = [];
    public Task InitializeAsync(string solutionPath, CancellationToken ct) => Initialization;

    public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes) =>
        new([TestProjects[0]], [TestProjects[0]], RequiresGraphRebuild: false, IsFullRun: false);

    public ImpactAnalysisResult AnalyzeFullRun() =>
        new(TestProjects, TestProjects, RequiresGraphRebuild: false, IsFullRun: true);

    public void InvalidateGraph() { }
    public IReadOnlyList<string> GetAllTestProjectPaths() => TestProjects;
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
    public bool AttachExecutionLog { get; set; }

    /// <summary>How many more runs <see cref="FailingTest"/> fails in before it passes; null fails it every time.</summary>
    public int? FailuresRemaining { get; set; }

    /// <summary>The framework unfiltered runs report for <see cref="FailingTest"/>, as a multi-targeted project's would.</summary>
    public string? FailingTestFramework { get; set; }

    /// <summary>Extra failing tests each project reports.</summary>
    public int ExtraFailures { get; set; }

    /// <summary>Every call: its targets, filter and whether it collected coverage.</summary>
    public List<(IReadOnlyList<string>? Targets, string? Filter, bool Coverage)> Calls { get; } = [];

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
        Calls.Add((testProjectPaths, filter, collectCoverage));
        DuringRun?.Invoke();
        var failing = FailingTest is not null && FailuresRemaining is not <= 0;
        if (failing && FailuresRemaining is { } remaining && testProjectPaths?.Any(p => FailingTest!.StartsWith(Path.GetFileNameWithoutExtension(p) + ".", StringComparison.Ordinal)) == true)
        {
            FailuresRemaining = remaining - 1;
        }

        if (RunningTest is { } running)
        {
            onProgress?.Invoke([new TestSuite("A.Tests",
                [new TestResult(running, running, TestStatus.Running, TimeSpan.Zero, null, null, null, null)],
                DateTimeOffset.UtcNow, TimeSpan.Zero)]);
            await Task.Delay(HoldRunning, ct);
        }

        var all = new List<TestSuite>();
        var projects = new List<ProjectTestResult>();
        void Complete(ProjectTestResult project)
        {
            projects.Add(project);
            onProjectCompleted?.Invoke(project);
        }

        foreach (var path in testProjectPaths ?? [])
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (CrashedProjects.Contains(path))
            {
                Complete(new ProjectTestResult(path, [], "crashed", [], Crashed: true));
                continue;
            }
            if (TimedOutProjects.TryGetValue(path, out var timeout))
            {
                var timedOutTest = new TestResult(timeout.Test, timeout.Test, TestStatus.Failed, timeout.Limit, null, "Timed out", null, null);
                var timedOutSuite = new TestSuite(name, [timedOutTest], DateTimeOffset.UtcNow, timeout.Limit);
                all.Add(timedOutSuite);
                Complete(new ProjectTestResult(path, [timedOutSuite], null, [], Crashed: false) { TimedOut = timeout });
                continue;
            }
            if (ProjectsWithoutResults.Contains(path))
            {
                Complete(new ProjectTestResult(path, [], null, [], Crashed: false));
                continue;
            }

            var fqn = $"{name}.Test1";
            var tests = new List<TestResult>
            {
                new(fqn, "Test1", failing && fqn == FailingTest ? TestStatus.Failed : TestStatus.Passed,
                    TimeSpan.FromMilliseconds(1), null, failing && fqn == FailingTest ? "boom" : null, null, null)
                {
                    TargetFramework = filter is null && fqn == FailingTest ? FailingTestFramework : null,
                },
            };
            for (var i = 0; i < ExtraFailures; i++)
            {
                tests.Add(new($"{name}.Extra{i}", $"Extra{i}", TestStatus.Failed, TimeSpan.FromMilliseconds(1), null, "boom", null, null));
            }

            if (DuplicateFirstTest && all.Count == 0)
            {
                tests.Add(tests[0]);
            }

            var suite = new TestSuite(name, tests, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(1))
            {
                ExecutionLog = AttachExecutionLog ? new ExecutionLog(path) : null,
            };
            all.Add(suite);
            Complete(new ProjectTestResult(path, [suite], null, [], Crashed: false));
        }

        return new TestRunResult(all, null, []) { Projects = projects };
    }
}
