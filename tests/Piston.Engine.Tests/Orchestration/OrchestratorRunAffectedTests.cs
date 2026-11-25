// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

/// <summary>Running affected tests waits for the engine's own run of an edit and reuses it when it tested the same files.</summary>
public sealed class OrchestratorRunAffectedTests : IDisposable
{
    private static readonly string[] Projects = ["/src/A.Tests/A.Tests.csproj", "/src/B.Tests/B.Tests.csproj"];

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-affected-run-{Guid.NewGuid():N}");
    private readonly PistonState _state = new();
    private readonly StubFileWatcherService _watcher = new();
    private readonly ProvenanceTestRunner _runner = new();
    private readonly ProvenanceImpactAnalyzer _impact = new(Projects);
    private readonly PistonOrchestrator _orchestrator;

    public OrchestratorRunAffectedTests()
    {
        _ = Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "App.slnx"), "<Solution />");
        File.WriteAllText(FileA, "class A { }");
        _orchestrator = new PistonOrchestrator(_watcher, new ProvenanceBuildService(), _runner, _impact, _state);
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

    [Fact]
    public async Task WhileTheRunOfTheEditIsActive_WaitsForItAndReusesIt()
    {
        await StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.DuringRun = () =>
        {
            _runner.DuringRun = null;
            entered.SetResult();
            _ = release.Task.Wait(TimeSpan.FromSeconds(10));
        };
        // The stub runner blocks synchronously, so keep it off the test thread.
        _ = Task.Run(() => _watcher.TriggerChange(Batch(FileA)), _ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        var affected = _orchestrator.RunAffectedAsync([FileA], _ct);
        Assert.False(affected.IsCompleted);
        release.SetResult();
        var result = await affected;

        Assert.True(result.Reused);
        Assert.Equal(TestRunTrigger.FileChange, result.Run!.Trigger);
        Assert.Equal(TestRunStatus.Completed, result.Run.Status);
        Assert.Equal(2, _runner.Calls.Count); // the startup run and the edit's run
    }

    [Fact]
    public async Task FullRunOfThePresentSource_IsReused()
    {
        await StartAsync();

        var result = await _orchestrator.RunAffectedAsync([FileA], _ct);

        Assert.True(result.Reused);
        Assert.Equal(TestRunTrigger.Startup, result.Run!.Trigger);
    }

    [Fact]
    public async Task LatestRunAnalysedOtherFiles_RunsTheSelectedTests()
    {
        await StartAsync();
        await ChangeAsync(FileB);
        _impact.PreviewResult = new ImpactAnalysisResult([], [Projects[1]], RequiresGraphRebuild: false, IsFullRun: false)
        {
            AffectedTestFqns = ["B.Tests.Test1"],
        };

        var result = await _orchestrator.RunAffectedAsync([FileA], _ct);

        Assert.False(result.Reused);
        Assert.Equal(TestRunTrigger.Targeted, result.Run!.Trigger);
        Assert.Equal(TestRunStatus.Completed, result.Run.Status);
        var (targets, filter, _) = _runner.Calls[^1];
        Assert.Equal([Projects[1]], targets);
        Assert.Contains("B.Tests.Test1", filter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceChangedSinceTheRunOfTheFiles_RunsAgain()
    {
        await StartAsync();
        await ChangeAsync(FileA);
        File.WriteAllText(FileA, "class Edited { }");

        var result = await _orchestrator.RunAffectedAsync([FileA], _ct);

        Assert.False(result.Reused);
        Assert.Equal(TestRunTrigger.Targeted, result.Run!.Trigger);
    }

    [Fact]
    public async Task RunNarrowedByTheUsersFilter_IsNotReused()
    {
        await StartAsync();
        _state.TestFilter = "Category=Fast";
        await ChangeAsync(FileA);

        var result = await _orchestrator.RunAffectedAsync([FileA], _ct);

        Assert.False(result.Reused);
    }

    [Fact]
    public async Task ChangesThatAffectNoTest_RunNothing()
    {
        await StartAsync();
        File.WriteAllText(FileA, "class Edited { }");
        _impact.PreviewResult = new ImpactAnalysisResult([], [], RequiresGraphRebuild: false, IsFullRun: false);

        var result = await _orchestrator.RunAffectedAsync([FileA], _ct);

        Assert.Null(result.Run);
        Assert.False(result.Reused);
        _ = Assert.Single(_runner.Calls);
    }

    [Fact]
    public async Task RunOfTheEditLeftNothingToSelect_IsStillReused()
    {
        await StartAsync();
        await ChangeAsync(FileA);
        // As the real analyzer after a run has measured the edited file: its content now matches the recording.
        _impact.PreviewResult = new ImpactAnalysisResult([], [], RequiresGraphRebuild: false, IsFullRun: false);

        var result = await _orchestrator.RunAffectedAsync([FileA], _ct);

        Assert.True(result.Reused);
        Assert.Equal(TestRunTrigger.FileChange, result.Run!.Trigger);
    }

    [Fact]
    public async Task FullPlanWhenARunStartedMeanwhile_WaitsForItInsteadOfSupersedingIt()
    {
        await StartAsync();
        File.WriteAllText(FileA, "class Edited { }");
        _impact.PreviewResult = new ImpactAnalysisResult(Projects, Projects, RequiresGraphRebuild: false, IsFullRun: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _impact.OnPreview = () =>
        {
            // The watcher raises another edit after the wait for active runs and before the full run is requested.
            _impact.OnPreview = null;
            _runner.DuringRun = () =>
            {
                _runner.DuringRun = null;
                entered.SetResult();
                _ = release.Task.Wait(TimeSpan.FromSeconds(10));
            };
            _ = Task.Run(() => _watcher.TriggerChange(Batch(FileB)));
            _ = entered.Task.Wait(TimeSpan.FromSeconds(10));
        };

        var affected = Task.Run(() => _orchestrator.RunAffectedAsync([FileA], _ct), _ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        // Give the request time to reach the active run; releasing earlier would only make the test pass trivially.
        await Task.Delay(200, _ct);
        release.SetResult();
        var result = await affected;

        Assert.Equal(TestRunStatus.Completed, Assert.Single(_state.RecentRuns, r => r.Trigger == TestRunTrigger.FileChange).Status);
        Assert.False(result.Reused);
        Assert.Equal(TestRunTrigger.Forced, result.Run!.Trigger);
        Assert.Equal(TestRunStatus.Completed, result.Run.Status);
    }

    [Fact]
    public async Task OwnRunFinishedOnASourceThatChangedWhileItRan_RunsAgain()
    {
        await StartAsync();
        File.WriteAllText(FileA, "class Edited { }");
        _runner.DuringRun = () =>
        {
            // An edit the watcher has not raised yet when the run finishes.
            _runner.DuringRun = null;
            File.WriteAllText(FileA, "class EditedAgain { }");
        };

        var result = await _orchestrator.RunAffectedAsync([FileA], _ct);

        Assert.Equal(ResultFreshness.Current, result.Run!.Freshness);
        Assert.Equal(3, _runner.Calls.Count); // the startup run, the stale run and its replacement
    }

    [Fact]
    public async Task RunOfAFileDifferingOnlyInCase_IsReusedOnlyWhereCaseIsInsignificant()
    {
        await StartAsync();
        await ChangeAsync(FileA);

        var result = await _orchestrator.RunAffectedAsync([Path.Combine(_root, "a.cs")], _ct);

        Assert.Equal(OperatingSystem.IsWindows(), result.Reused);
    }

    [Fact]
    public async Task OwnRunSupersededByTheRunOfANewerEdit_ReusesThatRun()
    {
        await StartAsync();
        await ChangeAsync(FileB);
        _runner.DuringRun = () =>
        {
            // The watcher raises the edit while the requested run is testing: its run supersedes this one.
            _runner.DuringRun = null;
            _watcher.TriggerChange(Batch(FileA));
        };

        var result = await _orchestrator.RunAffectedAsync([FileA], _ct);

        Assert.True(result.Reused);
        Assert.Equal(TestRunTrigger.FileChange, result.Run!.Trigger);
        Assert.Equal(TestRunStatus.Completed, result.Run.Status);
    }
}
