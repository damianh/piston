// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Piston.Engine.Verification;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class OrchestratorGraphRefreshTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", Guid.NewGuid().ToString("N"));
    private readonly PistonState _state = new();
    private readonly GraphWatcher _watcher = new();
    private readonly ProvenanceTestRunner _runner = new();
    private readonly PistonOrchestrator _orchestrator;
    private Action? _duringGraphRefresh;
    private string Solution => Path.Combine(_root, "App.slnx");
    private string FirstProject => Path.Combine(_root, "A.Tests.csproj");
    private string SecondProject => Path.Combine(_root, "B.Tests.csproj");

    public OrchestratorGraphRefreshTests()
    {
        MsBuildLocatorGuard.EnsureRegistered();
        _ = Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), "root = true\n");
        WriteProject(FirstProject, true);
        WriteProject(SecondProject, false);
        WriteSolution(false);
        _orchestrator = new PistonOrchestrator(_watcher, new ProvenanceBuildService(), _runner,
            new ImpactAnalyzer(path =>
            {
                _duringGraphRefresh?.Invoke();
                return new MsBuildSolutionGraph(path);
            }), _state);
    }

    public async ValueTask InitializeAsync()
    {
        await _orchestrator.StartAsync(Solution);
        _ = await WaitForRunAsync(TestRunTrigger.Startup);
        Assert.Equal([FirstProject], _state.LastRun!.Context!.TestProjects);
    }

    public async ValueTask DisposeAsync()
    {
        await _orchestrator.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private static void WriteProject(string path, bool isTest) => File.WriteAllText(path, $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <IsTestProject>{{isTest.ToString().ToLowerInvariant()}}</IsTestProject>
          </PropertyGroup>
        </Project>
        """);

    private void WriteSolution(bool includeSecond) => File.WriteAllText(Solution,
        """<Solution><Project Path="A.Tests.csproj" />"""
        + (includeSecond ? """<Project Path="B.Tests.csproj" />""" : "")
        + "</Solution>");

    private async Task<TestRunRecord> WaitForRunAsync(TestRunTrigger trigger)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (_state.LastRun?.Trigger != trigger)
        {
            await Task.Delay(10, timeout.Token);
        }
        return _state.LastRun;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SolutionAndProjectEdits_RefreshSelectionBeforeRun(bool projectEdit, bool watcherRun)
    {
        if (projectEdit)
        {
            WriteSolution(true);
            _ = await _orchestrator.RequestRunAsync();
            Assert.Equal([FirstProject], _state.LastRun!.Context!.TestProjects);
            WriteProject(SecondProject, true);
        }
        else
        {
            WriteProject(SecondProject, true);
            WriteSolution(true);
        }

        TestRunRecord run;
        if (watcherRun)
        {
            _watcher.Change(projectEdit ? SecondProject : Solution);
            run = await WaitForRunAsync(TestRunTrigger.FileChange);
        }
        else
        {
            run = (await _orchestrator.RequestRunAsync())!;
        }

        Assert.Contains(SecondProject, run.Context!.TestProjects!);
        Assert.Contains(run.Projects, p => p.ProjectPath == SecondProject);
        Assert.Equal(TestRunStatus.Completed, run.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAfterUnobservedGraphEdit_RefreshesForcedRunAndThenReusesIt(bool projectEdit)
    {
        if (projectEdit)
        {
            WriteSolution(true);
            _ = await _orchestrator.RequestRunAsync();
        }
        WriteProject(SecondProject, true);
        WriteSolution(true);

        var first = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);
        var second = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.Passed, first.Verdict);
        Assert.Equal(VerificationSource.Forced, first.Source);
        Assert.Equal([FirstProject, SecondProject], first.Run!.Context!.TestProjects!.Order());
        Assert.Equal(VerificationSource.Reused, second.Source);
        Assert.Equal(first.Run.RunId, second.Run!.RunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyRejectsCurrentCandidateMissingRefreshedTestProject(bool active)
    {
        WriteProject(SecondProject, true);
        WriteSolution(true);
        var fingerprint = SourceFingerprinter.Compute(_root);
        // Model a run from the old graph that nevertheless has the edited source fingerprint.
        var candidate = _state.LastRun! with
        {
            RunId = "old-graph",
            SourceBefore = fingerprint,
            SourceAfter = fingerprint,
            Status = active ? TestRunStatus.Running : TestRunStatus.Completed,
        };
        if (active)
        {
            _state.CurrentRun = candidate;
        }
        else
        {
            _state.RecentRuns = [candidate];
        }

        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.Passed, result.Verdict);
        Assert.Equal(VerificationSource.Forced, result.Source);
        Assert.NotEqual(candidate.RunId, result.Run!.RunId);
        Assert.Equal([FirstProject, SecondProject], result.Run.Context!.TestProjects!.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitoringFailureBeforeOrDuringRun_NeverPasses(bool duringRun)
    {
        if (duringRun)
        {
            File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Edited {}");
            _runner.DuringRun = () => _watcher.MonitoringError = "Watcher buffer overflow.";
        }
        else
        {
            _watcher.MonitoringError = "Watcher buffer overflow.";
        }

        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.Faulted, result.Verdict);
        Assert.Contains("Restart the daemon", result.Reason);
        Assert.Equal(duringRun ? 1 : 0, result.Attempts);
    }

    [Fact]
    public async Task MonitoringFailureDuringGraphRefresh_RejectsReuseBeforeDecision()
    {
        var startup = _state.LastRun;
        _duringGraphRefresh = () => _watcher.MonitoringError = "Watcher buffer overflow.";

        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.Faulted, result.Verdict);
        Assert.Contains("Restart the daemon", result.Reason);
        Assert.Equal(0, result.Attempts);
        Assert.Same(startup, _state.LastRun);
    }

    private sealed class GraphWatcher : IFileWatcherService
    {
        public event Action<FileChangeBatch>? FileChanged;
        public string? MonitoringError { get; set; }
        public void Start(string directoryPath) { }
        public void Stop() { }
        public void Dispose() { }
        public void Change(string path) => FileChanged?.Invoke(
            new FileChangeBatch([new FileChangeEvent(path, WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)],
                DateTimeOffset.UtcNow));
    }
}
