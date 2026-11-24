// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Piston.Engine.Tests.Orchestration;
using Xunit;

namespace Piston.Engine.Tests.Execution;

public sealed class OrchestratorRunDrainTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private static readonly string[] Projects = ["/src/A.Tests/A.Tests.csproj"];
    private readonly string _root = Directory.CreateTempSubdirectory("piston-orch-drain-").FullName;
    private readonly PistonState _state = new();
    private readonly ProvenanceTestRunner _runner = new();
    private readonly PistonOrchestrator _orchestrator;

    public OrchestratorRunDrainTests()
    {
        File.WriteAllText(SolutionPath, "<Solution />");
        _orchestrator = new PistonOrchestrator(
            new StubFileWatcherService(), new ProvenanceBuildService(), _runner, new ProvenanceImpactAnalyzer(Projects),
            _state, null, null, false, NullActivityEventSink.Instance);
    }

    private string SolutionPath => Path.Combine(_root, "App.slnx");

    public void Dispose()
    {
        _orchestrator.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_DrainsPipelineAndQueuedReplacement(bool synchronous)
    {
        var runner = new UnwindingTestRunner();
        var orchestrator = new PistonOrchestrator(
            new StubFileWatcherService(), new ProvenanceBuildService(), runner, new ProvenanceImpactAnalyzer(Projects),
            _state, null, null, false, NullActivityEventSink.Instance);
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

            runner.Release.SetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(15), _ct);
            Assert.Equal(TestRunStatus.Cancelled, _state.LastRun!.Status);
        }
        finally
        {
            _ = runner.Release.TrySetResult();
            await (disposal ?? orchestrator.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task FaultedRun_ReleasesTheLocalRunLock()
    {
        await _orchestrator.StartAsync(SolutionPath);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (_state.LastRun is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "startup run did not finish");
            await Task.Delay(10, _ct);
        }

        _runner.DuringRun = () => throw new InvalidOperationException("runner fault");
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => _orchestrator.RequestRunAsync());

        Assert.Equal(TestRunStatus.Faulted, _state.LastRun!.Status);
        _runner.DuringRun = null;
        Assert.Equal(TestRunStatus.Completed, (await _orchestrator.RequestRunAsync())!.Status);
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
}
