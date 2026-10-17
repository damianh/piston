// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Piston.Engine.Tests.Orchestration;
using Xunit;

namespace Piston.Engine.Tests.Execution;

/// <summary>Run lifecycle when machine-wide admission is in front of the pipeline.</summary>
public sealed class OrchestratorAdmissionTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static readonly string[] Projects = ["/src/A.Tests/A.Tests.csproj"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-orch-admission-{Guid.NewGuid():N}");
    private readonly ExecutionCoordinationDirectory _directory;
    private readonly PistonState _state = new();
    private readonly StubFileWatcherService _watcher = new();
    private readonly ProvenanceTestRunner _runner = new();
    private readonly PistonOrchestrator _orchestrator;

    public OrchestratorAdmissionTests()
    {
        _ = Directory.CreateDirectory(Path.Combine(_root, "solution"));
        File.WriteAllText(SolutionPath, "<Solution />");
        _directory = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        _directory.EnsureReady();
        File.WriteAllText(_directory.BudgetPath, """{ "cpuSlots": 1 }""");

        var coordinator = NewCoordinator();
        _orchestrator = new PistonOrchestrator(
            _watcher, new ProvenanceBuildService(), _runner, new ProvenanceImpactAnalyzer(Projects), _state,
            null, null, false, NullActivityEventSink.Instance, coordinator);
    }

    private string SolutionPath => Path.Combine(_root, "solution", "App.slnx");

    private MachineExecutionCoordinator NewCoordinator()
    {
        var coordinator = new MachineExecutionCoordinator(_directory, new ExecutionReservation(1, null), TimeSpan.FromMilliseconds(10));
        _ = coordinator.Initialize();
        return coordinator;
    }

    public void Dispose()
    {
        _orchestrator.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(10);
        }
    }

    private Task<IExecutionLease> OccupyBudget() =>
        NewCoordinator().AdmitAsync(Guid.NewGuid().ToString("N"), _ => { }, null, CancellationToken.None);

    private int Entries(string dir) => Directory.GetFiles(dir).Length;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_DrainsPipelineAndQueuedReplacement_BeforeReleasingAdmission(bool synchronous)
    {
        var runner = new UnwindingTestRunner();
        var orchestrator = new PistonOrchestrator(
            new StubFileWatcherService(), new ProvenanceBuildService(), runner, new ProvenanceImpactAnalyzer(Projects), _state,
            null, null, false, NullActivityEventSink.Instance, NewCoordinator());
        Task? disposal = null;
        try
        {
            await orchestrator.StartAsync(SolutionPath);
            await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(15), _ct);
            var replacement = orchestrator.RequestRunAsync();
            await runner.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15), _ct);

            disposal = synchronous ? Task.Run(orchestrator.Dispose, _ct) : orchestrator.DisposeAsync().AsTask();
            var cancelledReplacement = await replacement.WaitAsync(TimeSpan.FromSeconds(15), _ct);
            Assert.Equal(TestRunStatus.Cancelled, cancelledReplacement!.Status);
            Assert.Equal("Engine stopped before the run started.", cancelledReplacement.StatusReason);
            Assert.False(disposal.IsCompleted);
            Assert.Equal(1, Entries(_directory.LeasesDirectory));

            runner.Release.SetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(15), _ct);

            Assert.Equal(0, Entries(_directory.LeasesDirectory));
            Assert.Equal(0, Entries(_directory.QueueDirectory));
            Assert.Equal(TestRunStatus.Cancelled, _state.LastRun!.Status);
            using var free = await OccupyBudget();
        }
        finally
        {
            _ = runner.Release.TrySetResult();
            await (disposal ?? orchestrator.DisposeAsync().AsTask());
        }
    }

    private sealed class UnwindingTestRunner : ITestRunnerService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            using var registration = ct.Register(() => Cancelled.TrySetResult());
            Started.SetResult();
            await Release.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new TestRunResult([], null, []);
        }
    }

    [Fact]
    public async Task Run_IsPublishedAsQueued_WhileWaitingForAdmission_ThenRunsAdmitted()
    {
        var other = await OccupyBudget();

        await _orchestrator.StartAsync(SolutionPath);
        await WaitForAsync(() => _state.CurrentRun?.Admission is { State: TestRunAdmissionState.Queued },
            "startup run was not published as queued");

        var queued = _state.CurrentRun!;
        Assert.Equal(TestRunStatus.Queued, queued.Status);
        Assert.Null(queued.StartedAt);
        Assert.Null(queued.SourceBefore);
        Assert.Equal((1, 1, 1), (queued.Admission!.OtherAdmittedRuns, queued.Admission.CpuSlotsInUse, queued.Admission.BudgetCpuSlots));

        other.Dispose();
        await WaitForAsync(() => _state.LastRun is not null, "run did not finish after admission");

        var run = _state.LastRun!;
        Assert.Equal(TestRunStatus.Completed, run.Status);
        Assert.Equal(TestRunAdmissionState.Admitted, run.Admission!.State);
        _ = Assert.NotNull(run.Admission.AdmittedAt);
        Assert.True(run.StartedAt >= run.Admission.AdmittedAt);
        Assert.Equal(0, Entries(_directory.LeasesDirectory));
        Assert.Equal(0, Entries(_directory.QueueDirectory));
    }

    [Fact]
    public async Task Lease_IsHeldWhileExecuting_AndReleasedAfterCompletion()
    {
        var leasesDuringRun = -1;
        _runner.DuringRun = () => leasesDuringRun = Entries(_directory.LeasesDirectory);

        await _orchestrator.StartAsync(SolutionPath);
        await WaitForAsync(() => _state.LastRun is not null, "startup run did not finish");

        Assert.Equal(1, leasesDuringRun);
        Assert.Equal(0, Entries(_directory.LeasesDirectory));
    }

    [Fact]
    public async Task Lease_IsReleased_WhenTheRunFaults()
    {
        await _orchestrator.StartAsync(SolutionPath);
        await WaitForAsync(() => _state.LastRun is not null, "startup run did not finish");
        _runner.DuringRun = () => throw new InvalidOperationException("runner fault");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => _orchestrator.RequestRunAsync());

        Assert.Equal(TestRunStatus.Faulted, _state.LastRun!.Status);
        Assert.Equal(0, Entries(_directory.LeasesDirectory));
        _runner.DuringRun = null;
        Assert.Equal(TestRunStatus.Completed, (await _orchestrator.RequestRunAsync())!.Status);
    }

    [Fact]
    public async Task SupersededWhileQueued_IsCancelledWithoutStarting_AndReplacementKeepsQueuePosition()
    {
        var other = await OccupyBudget();
        await _orchestrator.StartAsync(SolutionPath);
        await WaitForAsync(() => _state.CurrentRun?.Admission is { State: TestRunAdmissionState.Queued },
            "startup run was not queued");
        var startup = _state.CurrentRun!;
        Assert.Equal(SolutionPath, startup.SolutionPath);
        Assert.Null(startup.Context);

        var forced = _orchestrator.RequestRunAsync();
        await WaitForAsync(() => _state.LastRun?.RunId == startup.RunId, "startup run was not superseded");

        var cancelled = _state.LastRun!;
        Assert.Equal(TestRunStatus.Cancelled, cancelled.Status);
        Assert.Equal(SolutionPath, cancelled.SolutionPath);
        Assert.Null(cancelled.Context);
        Assert.Equal("Superseded by a newer run while waiting for machine-wide execution admission.", cancelled.StatusReason);
        Assert.Null(cancelled.StartedAt);
        Assert.Equal(ResultFreshness.Unknown, cancelled.Freshness);

        await WaitForAsync(() => _state.CurrentRun is { Status: TestRunStatus.Queued, Admission: not null }
                                 && _state.CurrentRun.RunId != startup.RunId, "replacement was not queued");
        Assert.Equal(startup.Admission!.QueuedAt, _state.CurrentRun!.Admission!.QueuedAt);
        Assert.Equal(1, Entries(_directory.QueueDirectory));

        other.Dispose();
        var run = await forced.WaitAsync(TimeSpan.FromSeconds(15), _ct);
        Assert.Equal(TestRunStatus.Completed, run!.Status);
        Assert.Equal(0, Entries(_directory.QueueDirectory));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("dispose")]
    [InlineData("disposeAsync")]
    public async Task Shutdown_WhileQueued_CancelsAndRemovesTicket(string shutdown)
    {
        using var other = await OccupyBudget();
        await _orchestrator.StartAsync(SolutionPath);
        await WaitForAsync(() => _state.CurrentRun?.Admission is { State: TestRunAdmissionState.Queued },
            "startup run was not queued");

        switch (shutdown)
        {
            case "stop":
                _orchestrator.Stop();
                break;
            case "dispose":
                _orchestrator.Dispose();
                break;
            case "disposeAsync":
                await _orchestrator.DisposeAsync();
                break;
        }

        await WaitForAsync(() => _state.LastRun is not null, "queued run did not end");
        Assert.Equal(TestRunStatus.Cancelled, _state.LastRun!.Status);
        Assert.Equal("Engine stopped while waiting for machine-wide execution admission.", _state.LastRun.StatusReason);
        Assert.Null(_state.LastRun.StartedAt);
        Assert.Equal(0, Entries(_directory.QueueDirectory));
    }

    [Fact]
    public async Task AdmissionFailure_FaultsTheRunExplicitly()
    {
        using var other = await OccupyBudget();
        await _orchestrator.StartAsync(SolutionPath);
        await WaitForAsync(() => _state.CurrentRun?.Admission is { State: TestRunAdmissionState.Queued },
            "startup run was not queued");

        File.WriteAllText(_directory.BudgetPath, """{ "cpuSlots": 1, "surprise": true }""");

        await WaitForAsync(() => _state.LastRun is not null, "queued run did not end");
        var run = _state.LastRun!;
        Assert.Equal(TestRunStatus.Faulted, run.Status);
        Assert.Contains("surprise", run.StatusReason, StringComparison.Ordinal);
        Assert.Null(run.StartedAt);
        Assert.Equal(0, Entries(_directory.QueueDirectory));
    }
}
