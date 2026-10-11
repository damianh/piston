// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Execution;
using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests;

public sealed class PistonEngineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("piston-clear-test-").FullName;

    [Fact]
    public void OwnershipHandle_IsNotExposedToLibraryConsumers() => Assert.Null(typeof(PistonEngine).GetProperty("Ownership"));

    [Fact]
    public async Task DisposeAsync_HoldsWorktreeOwnership_UntilActivePipelineUnwinds()
    {
        var solution = Path.Combine(_root, "Test.slnx");
        File.WriteAllText(solution, "<Solution />");
        var directory = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        var engine = new PistonEngine(new PistonOptions
        {
            SolutionPath = solution,
            ProcessPoolSize = 1,
            ExecutionCoordinationDirectory = directory.BaseDirectory,
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var blocked = 0;
        engine.State.StateChanged += () =>
        {
            if (engine.State.Phase == PistonPhase.Building && Interlocked.Exchange(ref blocked, 1) == 0)
            {
                entered.SetResult();
                release.Wait();
                throw new OperationCanceledException();
            }
        };
        var starting = Task.Run(() => engine.StartAsync(solution));
        Task? disposing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            disposing = engine.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            _ = Assert.Throws<WorktreeOwnershipConflictException>(() => WorktreeOwnership.Acquire(directory, solution));
            _ = Assert.Single(Directory.GetFiles(directory.LeasesDirectory));

            release.Set();
            await starting.WaitAsync(TimeSpan.FromSeconds(30));
            await disposing.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Empty(Directory.GetFiles(directory.LeasesDirectory));
            using var free = WorktreeOwnership.Acquire(directory, solution);
        }
        finally
        {
            release.Set();
            await starting;
            await (disposing ?? engine.DisposeAsync().AsTask());
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

    [Theory]
    [InlineData("{ \"cpuSlots\": 0 }", 1)]
    [InlineData("{ \"cpuSlots\": 2 }", 3)]
    public async Task StartAsync_FailsExplicitly_ForInvalidOrUnsatisfiableBudget_AndHoldsNoOwnership(string budget, int poolSize)
    {
        var coordination = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        coordination.EnsureReady();
        File.WriteAllText(coordination.BudgetPath, budget);
        var solution = Path.Combine(_root, "Test.slnx");
        using var engine = new PistonEngine(new PistonOptions
        {
            SolutionPath = solution,
            ProcessPoolSize = poolSize,
            ExecutionCoordinationDirectory = coordination.BaseDirectory,
        });

        var ex = await Assert.ThrowsAsync<ExecutionCoordinationException>(() => engine.StartAsync(solution));

        Assert.Contains(coordination.BudgetPath, ex.Message, StringComparison.Ordinal);
        Assert.Null(engine.Ownership);
        using var free = WorktreeOwnership.Acquire(coordination, solution);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
