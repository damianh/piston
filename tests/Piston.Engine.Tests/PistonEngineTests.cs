// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Execution;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Piston.Engine.Tests.Orchestration;
using Xunit;

namespace Piston.Engine.Tests;

public sealed class PistonEngineTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateTempSubdirectory("piston-clear-test-").FullName;

    [Fact]
    public void OwnershipHandle_IsNotExposedToLibraryConsumers() => Assert.Null(typeof(PistonEngine).GetProperty("Ownership"));

    [Fact]
    public async Task RequiredTrust_DefersRepositoryAccessAndRunsUntilApproved()
    {
        var worktree = Path.Combine(_root, "untrusted");
        _ = Directory.CreateDirectory(Path.Combine(worktree, ".git"));
        var solution = Path.Combine(worktree, "Test.slnx");
        File.WriteAllText(solution, "<Solution />");
        var directory = new ExecutionCoordinationDirectory(Path.Combine(_root, "trust-coordination"));
        var buildService = new GatedBuildService();
        await using var engine = new PistonEngine(new PistonOptions
        {
            SolutionPath = solution,
            CoverageEnabled = false,
            HistoryEnabled = false,
            PersistResults = false,
            RequireTrust = true,
            RepositoryTrustStorePath = Path.Combine(_root, "trust", "repositories.json"),
            ExecutionCoordinationDirectory = directory.BaseDirectory,
        }, NullActivityEventSink.Instance, new EngineServiceOverrides(
            BuildService: buildService,
            ImpactAnalyzer: new StubImpactAnalyzer([]),
            SdkVersionProvider: _ => null));

        try
        {
            await engine.StartAsync(solution, () =>
            {
                Assert.Equal(PistonPhase.AwaitingTrust, engine.State.Phase);
                return Task.CompletedTask;
            });

            Assert.Equal(PistonPhase.AwaitingTrust, engine.State.Phase);
            Assert.Equal(WorktreeIdentity.CanonicalizePath(Path.Combine(worktree, ".git")), engine.State.RepositoryTrustPath);
            Assert.False(buildService.Started.Task.IsCompleted);
            Assert.False(Directory.Exists(Path.Combine(worktree, ".piston")));
            _ = await Assert.ThrowsAsync<RepositoryNotTrustedException>(() => engine.RequestRunAsync());
            _ = Assert.Throws<RepositoryNotTrustedException>(() => engine.Pause());
            _ = Assert.Throws<RepositoryNotTrustedException>(() => engine.Resume());

            engine.Stop();
            Assert.Equal(PistonPhase.AwaitingTrust, engine.State.Phase);
            await engine.GrantTrustAsync();
            await buildService.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), _ct);
            Assert.True(new RepositoryTrustStore(Path.Combine(_root, "trust", "repositories.json"))
                .IsTrusted(CloneIdentity.FromWorktreePath(worktree)));
            Assert.NotEqual(PistonPhase.AwaitingTrust, engine.State.Phase);
        }
        finally
        {
            buildService.Release();
        }
    }

    [Fact]
    public async Task DisposeAsync_HoldsWorktreeOwnership_UntilActivePipelineUnwinds()
    {
        var solution = Path.Combine(_root, "Test.slnx");
        File.WriteAllText(solution, "<Solution />");
        var directory = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        var buildService = new GatedBuildService();
        var engine = new PistonEngine(new PistonOptions
        {
            SolutionPath = solution,
            ProcessPoolSize = 1,
            ExecutionCoordinationDirectory = directory.BaseDirectory,
        }, NullActivityEventSink.Instance, new EngineServiceOverrides(
            BuildService: buildService,
            ImpactAnalyzer: new StubImpactAnalyzer([]),
            SdkVersionProvider: _ => null));
        var starting = engine.StartAsync(solution);
        Task? disposing = null;
        try
        {
            await buildService.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), _ct);
            disposing = engine.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            _ = Assert.Throws<WorktreeOwnershipConflictException>(() => WorktreeOwnership.Acquire(directory, solution));

            buildService.Release();
            await starting.WaitAsync(TimeSpan.FromSeconds(30), _ct);
            await disposing.WaitAsync(TimeSpan.FromSeconds(30), _ct);

            using var free = WorktreeOwnership.Acquire(directory, solution);
        }
        finally
        {
            buildService.Release();
            await starting;
            await (disposing ?? engine.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task IndependentWorktrees_ReachBuildConcurrently_AndIgnoreLegacyAdmissionFiles()
    {
        var directory = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        var legacyQueue = Path.Combine(directory.StateDirectory, "queue");
        var legacyLeases = Path.Combine(directory.StateDirectory, "leases");
        _ = Directory.CreateDirectory(legacyQueue);
        _ = Directory.CreateDirectory(legacyLeases);
        var oldTicket = Path.Combine(legacyQueue, "old.ticket");
        var oldLease = Path.Combine(legacyLeases, "old.lease");
        File.WriteAllText(oldTicket, "legacy ticket");
        File.WriteAllText(oldLease, "legacy lease");
        using var heldLease = ExclusiveFileLock.TryAcquire(oldLease, FileMode.Open);
        Assert.NotNull(heldLease);

        var gates = new List<GatedBuildService>();
        var engines = new List<PistonEngine>();
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var worktree = Path.Combine(_root, $"worktree-{index}");
                _ = Directory.CreateDirectory(Path.Combine(worktree, ".git"));
                var solution = Path.Combine(worktree, "Test.slnx");
                File.WriteAllText(solution, "<Solution />");
                var gate = new GatedBuildService();
                gates.Add(gate);
                var engine = new PistonEngine(new PistonOptions
                {
                    SolutionPath = solution,
                    ProcessPoolSize = Environment.ProcessorCount,
                    CoverageEnabled = false,
                    HistoryEnabled = false,
                    PersistResults = false,
                    ExecutionCoordinationDirectory = directory.BaseDirectory,
                }, NullActivityEventSink.Instance, new EngineServiceOverrides(
                    BuildService: gate,
                    ImpactAnalyzer: new StubImpactAnalyzer([]),
                    SdkVersionProvider: _ => null));
                engines.Add(engine);
                await engine.StartAsync(solution);
            }

            await Task.WhenAll(gates.Select(gate => gate.Started.Task)).WaitAsync(TimeSpan.FromSeconds(30), _ct);
            Assert.All(engines, engine => Assert.NotNull(engine.Ownership));
            Assert.Equal("legacy ticket", File.ReadAllText(oldTicket));
            Assert.True(File.Exists(oldLease));
        }
        finally
        {
            foreach (var gate in gates)
            {
                gate.Release();
            }
            foreach (var engine in engines)
            {
                await engine.DisposeAsync();
            }
        }
    }

    private sealed class GatedBuildService : IBuildService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct) =>
            BuildAsync(solutionPath, null, ct);

        public async Task<BuildResult> BuildAsync(string solutionPath, IReadOnlyList<string>? projectPaths, CancellationToken ct)
        {
            _ = Started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            throw new OperationCanceledException(ct);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearResultsClearsPersistedCoverageAndNotifiesWithResetState(bool coverageEnabled)
    {
        var filePath = Path.Combine(_root, "Code.cs");
        using var store = new SqliteCoverageStore();
        await store.InitializeAsync(_root);
        await store.StoreCoverageAsync(store.CreateRunId(),
            new Dictionary<string, IReadOnlyList<TestLineCoverage>>
            {
                ["Tests.Passes"] = [new(filePath, 1, 1)],
            });
        using var engine = new PistonEngine(new PistonOptions
        {
            SolutionPath = Path.Combine(_root, "Test.slnx"),
            CoverageEnabled = coverageEnabled,
        });
        engine.State.TestSuites = [new("Tests", [], DateTimeOffset.UtcNow, TimeSpan.Zero)];
        engine.State.LastRunTime = DateTimeOffset.UtcNow;
        engine.State.HasCoverageData = true;
        engine.State.CoverageImpactDetail = "Tier 3";
        engine.State.TestFilter = "KeepFilter";
        var notified = false;
        engine.State.StateChanged += () =>
        {
            notified = true;
            Assert.Empty(engine.State.TestSuites);
            Assert.Null(engine.State.LastRunTime);
            Assert.False(engine.State.HasCoverageData);
            Assert.Null(engine.State.CoverageImpactDetail);
            Assert.False(store.HasCoverageData(filePath));
        };

        engine.ClearResults();

        Assert.True(notified);
        Assert.Equal("KeepFilter", engine.State.TestFilter);
        Assert.Equal(coverageEnabled, engine.State.CoverageEnabled);
        Assert.Empty(store.GetTestsCoveringFile(filePath));
    }

    [Fact]
    public void ClearResultsWithoutDatabaseDoesNotCreateOne()
    {
        using var engine = new PistonEngine(new PistonOptions
        {
            SolutionPath = Path.Combine(_root, "Test.slnx"),
        });

        engine.ClearResults();
        engine.ClearResults();

        Assert.False(File.Exists(Path.Combine(_root, ".piston", "piston.db")));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("recent")]
    [InlineData("restored")]
    [InlineData("aggregate")]
    public void GetTestArtifact_ResolvesFilesFromRetainedResults(string resultSource)
    {
        const string runId = "0123456789abcdef0123456789abcdef";
        var source = Path.Combine(_root, "report.txt");
        File.WriteAllText(source, "test artifact");
        var suites = new TestArtifactStore(_root).Retain(runId,
        [
            new TestSuite("Tests",
            [
                new TestResult("Tests.Captures", "Captures", TestStatus.Passed, TimeSpan.Zero, null, null, null, null)
                {
                    Attachments = [new TestAttachment(source)],
                },
            ], DateTimeOffset.UnixEpoch, TimeSpan.Zero),
        ]);
        var run = new TestRunRecord
        {
            RunId = runId,
            EngineInstanceId = "engine",
            Sequence = 1,
            Trigger = TestRunTrigger.Forced,
            RequestedAt = DateTimeOffset.UnixEpoch,
            Status = resultSource == "active" ? TestRunStatus.Running : TestRunStatus.Completed,
            Restored = resultSource == "restored",
            Suites = suites,
        };
        using var engine = new PistonEngine(new PistonOptions
        {
            SolutionPath = Path.Combine(_root, "Test.slnx"),
        });
        if (resultSource == "active")
        {
            engine.State.CurrentRun = run;
        }
        else if (resultSource == "aggregate")
        {
            engine.State.TestSuites = suites;
        }
        else
        {
            engine.State.RecentRuns = [run];
        }

        var path = engine.GetTestArtifact(runId, 0);

        Assert.NotNull(path);
        Assert.Equal("test artifact", File.ReadAllText(path));
        Assert.Null(engine.GetTestArtifact(runId, 1));
        Assert.Null(engine.GetTestArtifact("fedcba9876543210fedcba9876543210", 0));
    }

    [Fact]
    public async Task StartAsync_RefusesAWorktreeOwnedByAnotherEngine_WithoutStarting()
    {
        var coordination = Path.Combine(_root, "coordination");
        var solution = Path.Combine(_root, "Test.slnx");
        File.WriteAllText(solution, "<Solution />");
        var directory = new ExecutionCoordinationDirectory(coordination);
        directory.EnsureReady();
        using var owner = WorktreeOwnership.Acquire(directory, solution);
        using var engine = new PistonEngine(new PistonOptions
        {
            SolutionPath = solution,
            ExecutionCoordinationDirectory = coordination,
        });

        var ex = await Assert.ThrowsAsync<WorktreeOwnershipConflictException>(() => engine.StartAsync(solution));

        Assert.Equal(Environment.ProcessId, ex.Owner?.ProcessId);
        Assert.Null(engine.Ownership);
        Assert.Equal(PistonPhase.Idle, engine.State.Phase);
    }

    [Fact]
    public async Task StartAsync_WhenBeforeRunsStartFails_StartsNoRunAndReleasesOwnership()
    {
        var coordination = Path.Combine(_root, "coordination");
        var solution = Path.Combine(_root, "Test.slnx");
        File.WriteAllText(solution, "<Solution />");
        var directory = new ExecutionCoordinationDirectory(coordination);
        using var engine = new PistonEngine(new PistonOptions
        {
            SolutionPath = solution,
            ProcessPoolSize = 1,
            ExecutionCoordinationDirectory = coordination,
        });
        var phases = new List<PistonPhase>();
        engine.State.StateChanged += () => phases.Add(engine.State.Phase);
        var heldOwnershipDuringCallback = false;

        var ex = await Assert.ThrowsAsync<IOException>(() => engine.StartAsync(solution, () =>
        {
            heldOwnershipDuringCallback = engine.Ownership is not null;
            throw new IOException("Address already in use");
        }));

        Assert.Equal("Address already in use", ex.Message);
        Assert.True(heldOwnershipDuringCallback);
        Assert.Empty(phases);
        Assert.Null(engine.State.CurrentRun);
        Assert.Null(engine.State.LastRun);
        Assert.Null(engine.Ownership);
        using var free = WorktreeOwnership.Acquire(directory, solution);
    }

    [Theory]
    [InlineData("{ \"cpuSlots\": 0 }")]
    [InlineData("{ \"cpuSlots\": 2, \"containerSlots\": 1 }")]
    [InlineData("not json")]
    public async Task StartAsync_RejectsLegacyBudget_WithoutModifyingItOrHoldingOwnership(string budget)
    {
        var coordination = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        coordination.EnsureReady();
        File.WriteAllText(coordination.LegacyBudgetPath, budget);
        var solution = Path.Combine(_root, "Test.slnx");
        File.WriteAllText(solution, "<Solution />");
        var buildService = new GatedBuildService();
        using var engine = new PistonEngine(new PistonOptions
        {
            SolutionPath = solution,
            CoverageEnabled = false,
            HistoryEnabled = false,
            PersistResults = false,
            ExecutionCoordinationDirectory = coordination.BaseDirectory,
        }, NullActivityEventSink.Instance, new EngineServiceOverrides(
            BuildService: buildService,
            ImpactAnalyzer: new StubImpactAnalyzer([]),
            SdkVersionProvider: _ => null));

        var ex = await Assert.ThrowsAsync<ExecutionCoordinationException>(() => engine.StartAsync(solution));

        Assert.Contains(coordination.LegacyBudgetPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Remove", ex.Message, StringComparison.Ordinal);
        Assert.Equal(budget, File.ReadAllText(coordination.LegacyBudgetPath));
        Assert.Null(engine.Ownership);
        using (WorktreeOwnership.Acquire(coordination, solution))
        {
        }

        File.Delete(coordination.LegacyBudgetPath);
        try
        {
            await engine.StartAsync(solution);
            await buildService.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), _ct);
            Assert.NotNull(engine.Ownership);
        }
        finally
        {
            buildService.Release();
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
