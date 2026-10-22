// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Orchestration;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class OrchestratorCoverageTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public void CoverageResultsDirectory_FindsThePistonTempDirectoryAtAnyDepth()
    {
        var results = Path.Combine(Path.GetTempPath(), "piston-abc");

        Assert.Equal(results, PistonOrchestrator.CoverageResultsDirectory(Path.Combine(results, "r.cobertura.xml")));
        Assert.Equal(results, PistonOrchestrator.CoverageResultsDirectory(Path.Combine(results, "guid", "coverage.cobertura.xml")));
        Assert.Null(PistonOrchestrator.CoverageResultsDirectory(Path.Combine(Path.GetTempPath(), "other", "r.cobertura.xml")));
        Assert.Null(PistonOrchestrator.CoverageResultsDirectory(Path.Combine(results, "..", "r.cobertura.xml")));
    }

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
            await processor.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
            Assert.True(store.HasCoverageData(file));

            var clearingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clearing = Task.Run(() =>
            {
                clearingStarted.SetResult();
                orchestrator.ClearResults(root);
            }, _ct);
            try
            {
                await clearingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
                Assert.NotSame(clearing, await Task.WhenAny(clearing, Task.Delay(100, _ct)));
            }
            finally
            {
                _ = processor.Release.TrySetResult();
                await Task.WhenAll(processing, clearing).WaitAsync(TimeSpan.FromSeconds(5), _ct);
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

    [Fact]
    public async Task GetFileCoverage_FlagsFilesEditedSinceMeasurementAsStale()
    {
        var root = Directory.CreateTempSubdirectory("piston-coverage-query-").FullName;
        try
        {
            var file = Path.Combine(root, "Code.cs");
            await File.WriteAllTextAsync(file, "class C {}", _ct);
            using var store = new SqliteCoverageStore();
            await store.InitializeAsync(root);
            await store.StoreReportAsync(store.CreateRunId(), [
                new CoverageReportFile(
                    new FileCoverage(file, [new LineCoverage(1, 2), new LineCoverage(2, 0)], "Lib"),
                    CoverageReportFile.ComputeContentHash(file)),
            ], isFullRun: true);
            using var orchestrator = new PistonOrchestrator(
                new StubFileWatcherService(), new StubBuildService(),
                new StubTestRunnerService([], TimeSpan.Zero), new StubImpactAnalyzer([]),
                new PistonState(), store, new CoverageProcessor(new CoberturaParser()), true);

            var fresh = orchestrator.GetFileCoverage(file)!;
            Assert.False(fresh.File.IsStale);
            Assert.Equal([new LineCoverage(1, 2), new LineCoverage(2, 0)], fresh.Lines);
            var report = orchestrator.GetCoverageReport()!;
            Assert.Equal((2, 1), (report.LinesTotal, report.LinesCovered));
            Assert.Null(orchestrator.GetFileCoverage(Path.Combine(root, "Other.cs")));

            await File.WriteAllTextAsync(file, "class C { int x; }", _ct);

            Assert.True(orchestrator.GetFileCoverage(file)!.File.IsStale);
            Assert.True(Assert.Single(orchestrator.GetCoverageReport()!.Files).IsStale);
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
            long runId, IReadOnlyList<string> paths, IReadOnlyList<string> testFqns, ICoverageStore store, bool isFullRun)
        {
            await store.StoreCoverageAsync(runId,
                new Dictionary<string, IReadOnlyList<TestLineCoverage>>
                {
                    [testFqns[0]] = [new(file, 1, 1)],
                });
            _ = Committed.TrySetResult();
            await Release.Task;
        }
    }
}
