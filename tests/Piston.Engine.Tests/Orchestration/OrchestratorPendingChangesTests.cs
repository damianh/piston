// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

/// <summary>Changed files stay pending until a completed run has analysed them.</summary>
public sealed class OrchestratorPendingChangesTests : IDisposable
{
    private static readonly string[] Projects = ["/src/A.Tests/A.Tests.csproj", "/src/B.Tests/B.Tests.csproj"];

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-pending-{Guid.NewGuid():N}");
    private readonly PistonState _state = new();
    private readonly StubFileWatcherService _watcher = new();
    private readonly ProvenanceBuildService _build = new();
    private readonly ProvenanceTestRunner _runner = new();
    private readonly ProvenanceImpactAnalyzer _impact = new(Projects);
    private readonly PistonOrchestrator _orchestrator;

    public OrchestratorPendingChangesTests()
    {
        _ = Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "App.slnx"), "<Solution />");
        _orchestrator = new PistonOrchestrator(_watcher, _build, _runner, _impact, _state);
    }

    public void Dispose()
    {
        _orchestrator.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string FileA => Path.Combine(_root, "A.cs");
    private string FileB => Path.Combine(_root, "B.cs");

    private async Task StartAsync()
    {
        await _orchestrator.StartAsync(Path.Combine(_root, "App.slnx"));
        await _orchestrator.AwaitPipelinesDrainedAsync(_ct);
    }

    private async Task ChangeAsync(string file)
    {
        _watcher.TriggerChange(Batch(file));
        await _orchestrator.AwaitPipelinesDrainedAsync(_ct);
    }

    private static FileChangeBatch Batch(string file) =>
        new([new FileChangeEvent(file, WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)], DateTimeOffset.UtcNow);

    /// <summary>Holds the next test execution open; returns once it has started and a function that releases it.</summary>
    private async Task<Action> HoldNextTestExecutionAsync(Action start)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.DuringRun = () =>
        {
            _runner.DuringRun = null;
            entered.SetResult();
            _ = release.Task.Wait(TimeSpan.FromSeconds(10));
        };

        // The stub runner blocks synchronously, so keep it off the test thread.
        _ = Task.Run(start, _ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        return release.SetResult;
    }

    [Fact]
    public async Task ChangeDuringARun_IsAnalysedWithTheSupersededRunsFiles()
    {
        await StartAsync();
        var release = await HoldNextTestExecutionAsync(() => _watcher.TriggerChange(Batch(FileA)));

        _watcher.TriggerChange(Batch(FileB));
        release();
        await _orchestrator.AwaitPipelinesDrainedAsync(_ct);

        Assert.Equal([FileA, FileB], _impact.AnalyzedFiles[^1].Order(StringComparer.Ordinal));
        var last = _state.LastRun!;
        Assert.Equal(TestRunStatus.Completed, last.Status);
        Assert.Equal([FileA, FileB], last.ChangedFiles.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ChangeAfterACompletedRun_IsAnalysedAlone()
    {
        await StartAsync();
        await ChangeAsync(FileA);

        await ChangeAsync(FileB);

        Assert.Equal([FileB], _impact.AnalyzedFiles[^1]);
    }

    [Fact]
    public async Task ChangeAfterAFailedBuild_IsAnalysedWithTheUntestedFiles()
    {
        await StartAsync();
        _build.Fail = true;
        await ChangeAsync(FileA);
        Assert.Equal(TestRunStatus.BuildFailed, _state.LastRun!.Status);
        _build.Fail = false;

        await ChangeAsync(FileB);

        Assert.Equal([FileA, FileB], _impact.AnalyzedFiles[^1].Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ChangeToAFileDifferingOnlyInCase_ReplacesThePendingFileOnlyWhereCaseIsInsignificant()
    {
        await StartAsync();
        var lower = Path.Combine(_root, "a.cs");
        var release = await HoldNextTestExecutionAsync(() => _watcher.TriggerChange(Batch(FileA)));

        _watcher.TriggerChange(Batch(lower));
        release();
        await _orchestrator.AwaitPipelinesDrainedAsync(_ct);

        string[] expected = OperatingSystem.IsWindows() ? [lower] : [FileA, lower];
        Assert.Equal(expected, _impact.AnalyzedFiles[^1].Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CompletedFullRun_CoversChangesOfTheRunItSuperseded()
    {
        await StartAsync();
        var release = await HoldNextTestExecutionAsync(() => _watcher.TriggerChange(Batch(FileA)));
        var full = _orchestrator.RequestRunAsync();
        release();
        Assert.Equal(TestRunStatus.Completed, (await full)!.Status);
        await _orchestrator.AwaitPipelinesDrainedAsync(_ct);

        await ChangeAsync(FileB);

        Assert.Equal([FileB], _impact.AnalyzedFiles[^1]);
    }
}
