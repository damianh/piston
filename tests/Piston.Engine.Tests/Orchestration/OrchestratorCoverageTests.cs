using Piston.Engine.Coverage;
using Piston.Engine.Orchestration;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class OrchestratorCoverageTests
{
    [Fact]
    public async Task ClearResultsSerializesWithCommittedCoverageBeforeAvailabilityPublication()
    {
        var root = Directory.CreateTempSubdirectory("piston-coverage-clear-race-").FullName;
        try
        {
            using var store = new SqliteCoverageStore();
            await store.InitializeAsync(root);
            var file = Path.Combine(root, "Code.cs");
            var processor = new PausingCoverageProcessor(file);
            var state = new PistonState();
            using var orchestrator = new PistonOrchestrator(
                new StubFileWatcherService(), new StubBuildService(),
                new StubTestRunnerService([], TimeSpan.Zero), new StubImpactAnalyzer([]),
                state, store, processor, true);

            var processing = orchestrator.ProcessCoverageAsync(store.CreateRunId(), [], ["Tests.Pass"]);
            await processor.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(store.HasCoverageData(file));

            var clearingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clearing = Task.Run(() =>
            {
                clearingStarted.SetResult();
                orchestrator.ClearResults(root);
            });
            try
            {
                await clearingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotSame(clearing, await Task.WhenAny(clearing, Task.Delay(100)));
            }
            finally
            {
                processor.Release.TrySetResult();
                await Task.WhenAll(processing, clearing).WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.False(state.HasCoverageData);
            Assert.False(store.HasCoverageData(file));

            // A genuinely later completion may publish fresh coverage.
            await orchestrator.ProcessCoverageAsync(store.CreateRunId(), [], ["Tests.Pass"]);
            Assert.True(state.HasCoverageData);
            Assert.True(store.HasCoverageData(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PausingCoverageProcessor(string file) : ICoverageProcessor
    {
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ProcessCoverageAsync(
            long runId, IReadOnlyList<string> paths, IReadOnlyList<string> testFqns, ICoverageStore store)
        {
            await store.StoreCoverageAsync(runId,
                new Dictionary<string, IReadOnlyList<TestLineCoverage>>
                {
                    [testFqns[0]] = [new(file, 1, 1)],
                });
            Committed.TrySetResult();
            await Release.Task;
        }
    }
}
