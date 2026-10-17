// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Piston.Engine.Verification;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class OrchestratorVerifyTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static readonly string[] Projects = ["/src/A.Tests/A.Tests.csproj", "/src/B.Tests/B.Tests.csproj"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-verify-{Guid.NewGuid():N}");
    private readonly PistonState _state = new();
    private readonly StubFileWatcherService _watcher = new();
    private readonly ProvenanceBuildService _build = new();
    private readonly ProvenanceTestRunner _runner = new();
    private readonly ProvenanceImpactAnalyzer _impact = new(Projects);
    private readonly PistonOrchestrator _orchestrator;

    public OrchestratorVerifyTests()
    {
        _ = Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "App.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Code { }");
        _orchestrator = new PistonOrchestrator(_watcher, _build, _runner, _impact, _state);
    }

    public void Dispose()
    {
        _orchestrator.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task StartAsync()
    {
        await _orchestrator.StartAsync(Path.Combine(_root, "App.slnx"));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_state.LastRun is not { Trigger: TestRunTrigger.Startup })
        {
            Assert.True(DateTime.UtcNow < deadline, "Startup run did not finish.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task NotStarted_IsFaulted()
    {
        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);
        Assert.Equal(VerificationVerdict.Faulted, result.Verdict);
    }

    [Fact]
    public async Task SecondVerifyWithoutEdits_ReusesSameRun()
    {
        await StartAsync();

        var first = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);
        var second = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.Passed, first.Verdict);
        Assert.Equal(VerificationSource.Reused, first.Source); // the startup run already qualifies
        Assert.Equal(VerificationSource.Reused, second.Source);
        Assert.Equal(first.Run!.RunId, second.Run!.RunId);
        Assert.Equal(_state.LastRun!.RunId, second.Run.RunId);
    }

    [Fact]
    public async Task ExternalBuildInputs_NeverReuseOrStartVerificationRun()
    {
        await StartAsync();
        var startup = _state.LastRun!;
        _impact.VerificationCoverage = () => "External import is outside the source fingerprint.";

        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.NotFullScope, result.Verdict);
        Assert.Equal(0, result.Attempts);
        Assert.Null(result.Run);
        Assert.Same(startup, _state.LastRun);
    }

    [Fact]
    public async Task ExternalInputRemovedWithoutFingerprintChange_DoesNotReuseStartupRun()
    {
        // The startup run built while an external input (e.g. an ancestor .editorconfig) existed.
        _impact.VerificationCoverage = () => "Ancestor .editorconfig is outside the source fingerprint.";
        await StartAsync();
        var startup = _state.LastRun!;
        Assert.False(startup.InputCoverageVerified);

        // The external input is deleted: the fingerprint is unchanged but coverage now succeeds.
        _impact.VerificationCoverage = () => null;
        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.Passed, result.Verdict);
        Assert.Equal(VerificationSource.Forced, result.Source);
        Assert.NotEqual(startup.RunId, result.Run!.RunId);
        Assert.Equal(startup.SourceAfter, result.Run.SourceAfter);
        Assert.True(result.Run.InputCoverageVerified);
    }

    [Fact]
    public async Task EditBetweenReuseDecisionAndReturn_RechecksFingerprint()
    {
        await StartAsync();
        var startup = _state.LastRun!;
        var checks = 0;
        _impact.VerificationCoverage = () =>
        {
            if (checks++ == 0)
            {
                File.WriteAllText(Path.Combine(_root, "Code.cs"), "class EditedDuringReuse { }");
            }
            return null;
        };

        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.Passed, result.Verdict);
        Assert.NotEqual(startup.RunId, result.Run!.RunId);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(SourceFingerprinter.Compute(_root), result.SourceFingerprint);
    }

    [Fact]
    public async Task AfterEdit_RequestsForcedRunBoundToEditedSource()
    {
        await StartAsync();
        var startup = _state.LastRun!;
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Edited { }");

        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.Passed, result.Verdict);
        Assert.Equal(VerificationSource.Forced, result.Source);
        Assert.NotEqual(startup.RunId, result.Run!.RunId);
        Assert.Equal(SourceFingerprinter.Compute(_root), result.SourceFingerprint);
        Assert.Equal(result.SourceFingerprint, result.Run.SourceAfter);
    }

    [Fact]
    public async Task SourceEditDuringCoverageEvaluation_RetriesBeforeAcceptingRun()
    {
        await StartAsync();
        var projectPath = Path.Combine(_root, "App.csproj");
        File.WriteAllText(projectPath, "<Project />");
        var coverageChecks = 0;
        _impact.VerificationCoverage = () =>
        {
            if (Interlocked.Increment(ref coverageChecks) == 1)
            {
                File.WriteAllText(projectPath,
                    """<Project><ItemGroup><ProjectReference Include="../External/External.csproj" /></ItemGroup></Project>""");
                return null;
            }

            return "A newly added ProjectReference is outside the source fingerprint.";
        };

        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.NotFullScope, result.Verdict);
        Assert.Equal(1, result.Attempts);
        Assert.Null(result.Run);
        // Verification re-evaluates once; the watcher-triggered full run may also capture its own coverage.
        Assert.True(coverageChecks >= 2, $"coverage checks: {coverageChecks}");
    }

    [Fact]
    public async Task FailingTest_IsTestsFailed()
    {
        await StartAsync();
        _runner.FailingTest = "A.Tests.Test1";
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Edited { }");

        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.TestsFailed, result.Verdict);
        Assert.True(result.Run!.IsCurrentFullResult);
    }

    [Fact]
    public async Task BuildFailure_IsBuildFailedWithRunDiagnostics_AndIsReused()
    {
        await StartAsync();
        _build.Fail = true;
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Broken {");

        var first = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);
        var second = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.BuildFailed, first.Verdict);
        Assert.Contains("CS0001: boom", first.Run!.Build!.Errors);
        Assert.Equal(VerificationSource.Reused, second.Source);
        Assert.Equal(first.Run.RunId, second.Run!.RunId);
    }

    [Fact]
    public async Task EditDuringRun_RetriesUntilResultIsCurrent()
    {
        await StartAsync();
        var edits = 0;
        _runner.DuringRun = () =>
        {
            if (edits++ == 0)
            {
                File.WriteAllText(Path.Combine(_root, "Code.cs"), "class DuringRun { }");
            }
        };
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Edited { }");

        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.Passed, result.Verdict);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(SourceFingerprinter.Compute(_root), result.Run!.SourceAfter);
    }

    [Fact]
    public async Task ContinuousEdits_GiveUpWithoutPassing()
    {
        await StartAsync();
        _orchestrator.MaxVerificationAttempts = 2;
        var edits = 0;
        _runner.DuringRun = () => File.WriteAllText(Path.Combine(_root, "Code.cs"), $"class C{edits++} {{ }}");
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Edited { }");

        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.Stale, result.Verdict);
        Assert.Equal(2, result.Attempts);
        Assert.Contains("Gave up", result.Reason);
    }

    [Fact]
    public async Task ContinuousEdits_DefaultBoundIsTwoRetries()
    {
        await StartAsync();
        var edits = 0;
        _runner.DuringRun = () => File.WriteAllText(Path.Combine(_root, "Code.cs"), $"class C{edits++} {{ }}");
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Edited { }");

        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.Stale, result.Verdict);
        Assert.Equal(3, result.Attempts);
    }

    [Fact]
    public async Task CancelledOnEveryAttempt_ExhaustsAsStaleNotCancelled()
    {
        await StartAsync();
        _runner.DuringRun = () => throw new OperationCanceledException();
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Edited { }");

        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.Stale, result.Verdict);
        Assert.Equal(3, result.Attempts);
        Assert.Contains("Gave up", result.Reason);
        Assert.Equal(TestRunStatus.Cancelled, result.Run!.Status);
    }

    [Fact]
    public async Task SupersededForcedRun_IsRetried()
    {
        await StartAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        _runner.DuringRun = () =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                // Simulates the agent's own edit being raised by the watcher mid-run: the watcher
                // trigger supersedes (cancels) the forced run.
                _watcher.TriggerChange(new FileChangeBatch(
                    [new FileChangeEvent(Path.Combine(_root, "Code.cs"), WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)], DateTimeOffset.UtcNow));
                _ = release.Task.Wait(TimeSpan.FromSeconds(5));
                throw new OperationCanceledException();
            }
        };
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Edited { }");

        var verify = Task.Run(() => _orchestrator.VerifyAsync(Timeout, CancellationToken.None));
        release.SetResult();
        var result = await verify;

        Assert.Equal(VerificationVerdict.Passed, result.Verdict);
        Assert.True(result.Attempts >= 2);
        Assert.True(result.Run!.IsCurrentFullResult);
    }

    [Fact]
    public async Task UserFilter_IsNotFullScope()
    {
        await StartAsync();
        _state.TestFilter = "Category=Fast";

        var result = await _orchestrator.VerifyAsync(Timeout, CancellationToken.None);

        Assert.Equal(VerificationVerdict.NotFullScope, result.Verdict);
        Assert.Null(result.Run);
    }

    [Fact]
    public async Task Timeout_ReturnsTimedOutAndRunContinues()
    {
        await StartAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.DuringRun = () => release.Task.Wait(TimeSpan.FromSeconds(10));
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Edited { }");

        // The stub runner blocks synchronously, so keep it off the test thread.
        var result = await Task.Run(() => _orchestrator.VerifyAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None));
        release.SetResult();

        Assert.Equal(VerificationVerdict.TimedOut, result.Verdict);
        Assert.NotNull(result.Run);
    }

    [Fact]
    public async Task ActiveForcedRun_IsAwaitedNotDuplicated()
    {
        await StartAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        _runner.DuringRun = () =>
        {
            _ = Interlocked.Increment(ref runs);
            _ = release.Task.Wait(TimeSpan.FromSeconds(10));
        };

        var forced = Task.Run(_orchestrator.RequestRunAsync);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref runs) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(10, _ct);
        }

        var verify = Task.Run(() => _orchestrator.VerifyAsync(Timeout, CancellationToken.None));
        await Task.Delay(50, _ct);
        release.SetResult();
        var run = await forced;
        var result = await verify;

        Assert.Equal(VerificationSource.AwaitedActive, result.Source);
        Assert.Equal(run!.RunId, result.Run!.RunId);
        Assert.Equal(1, runs);
    }
}
