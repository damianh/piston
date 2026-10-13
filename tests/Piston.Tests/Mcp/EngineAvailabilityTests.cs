// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using ModelContextProtocol;
using Piston.Engine;
using Piston.Engine.Execution;
using Piston.Engine.Verification;
using Piston.Mcp;
using Piston.Mcp.Tools;
using Xunit;

namespace Piston.Tests.Mcp;

public sealed class EngineAvailabilityTests
{
    private static readonly WorktreeIdentity Worktree = new("/work/wt", "0123456789abcdef0123456789abcdef");

    [Fact]
    public void Available_ByDefault()
    {
        var gate = new EngineAvailability();
        Assert.Equal(EngineAvailabilityState.Available, gate.State);
        Assert.Null(gate.Reason);
        gate.ThrowIfUnavailable();
    }

    [Fact]
    public void OwnedElsewhere_NamesOwnerAndFallback()
    {
        var gate = new EngineAvailability();
        gate.SetOwnedElsewhere(Worktree,
            new ExecutionOwnerInfo(4242, "/work/wt/App.slnx", "/work/wt", DateTimeOffset.UnixEpoch, "daemon"));

        var ex = Assert.Throws<McpException>(gate.ThrowIfUnavailable);
        Assert.StartsWith("Piston owned by another process (PID 4242, daemon, solution '/work/wt/App.slnx'", ex.Message);
        Assert.Contains("/work/wt", ex.Message);
        Assert.EndsWith("Use `dotnet test` instead.", ex.Message);
    }

    [Fact]
    public void OwnedElsewhere_WithoutDetails_StillActionable()
    {
        var gate = new EngineAvailability();
        gate.SetOwnedElsewhere(Worktree, null);
        Assert.Contains("details unavailable", gate.Reason);
        Assert.EndsWith(EngineAvailability.Fallback, gate.Reason);
    }

    [Fact]
    public void WorktreeDeletedAndFaulted_AreActionable()
    {
        var deleted = new EngineAvailability();
        deleted.SetWorktreeDeleted("/work/gone");
        Assert.Equal(EngineAvailabilityState.WorktreeDeleted, deleted.State);
        Assert.Contains("'/work/gone' no longer exists", deleted.Reason);

        var faulted = new EngineAvailability();
        faulted.SetFaulted("No solution found.");
        Assert.Equal("Piston could not start: No solution found. Use `dotnet test` instead.", faulted.Reason);
    }

    [Fact]
    public void Stopped_DoesNotOverrideSpecificReason()
    {
        var gate = new EngineAvailability();
        gate.SetWorktreeDeleted("/work/gone");
        gate.SetStopped();
        Assert.Equal(EngineAvailabilityState.WorktreeDeleted, gate.State);

        var running = new EngineAvailability();
        running.SetStopped();
        Assert.Equal(EngineAvailabilityState.Stopped, running.State);
        _ = Assert.Throws<McpException>(running.ThrowIfUnavailable);
    }

    [Fact]
    public async Task EveryTool_FailsWithReason_WhenUnavailable()
    {
        var engine = new StubEngine();
        var gate = new EngineAvailability();
        gate.SetOwnedElsewhere(Worktree, null);
        var tools = new TestTools(engine, NullMcpCallRecorder.Instance, gate);

        var calls = new Func<Task>[]
        {
            () => tools.VerifyAsync(ct: CancellationToken.None),
            () => tools.RunTestsAsync(ct: CancellationToken.None),
            () => Task.FromResult(tools.GetTestResults()),
            () => Task.FromResult(tools.GetTestRun()),
            () => Task.FromResult(tools.SetTestFilter("x")),
            () => Task.FromResult(tools.ClearResults()),
        };

        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<McpException>(call);
            Assert.Equal(gate.Reason, ex.Message);
        }

        Assert.False(engine.ForceRunCalled);
        Assert.False(engine.ClearResultsCalled);
        Assert.Null(engine.LastFilter);
    }

    [Fact]
    public async Task Verify_ReportsSolution()
    {
        var engine = new StubEngine
        {
            NextVerification = new VerificationResult(VerificationVerdict.Passed, null, VerificationSource.Reused, 1, null, null),
        };
        engine.State.SolutionPath = Path.Combine(Path.GetTempPath(), "wt", "App.slnx");
        var text = await new TestTools(engine, NullMcpCallRecorder.Instance).VerifyAsync(5, ct: CancellationToken.None);

        Assert.StartsWith("Verdict: Passed", text);
        Assert.Contains($"Solution: {engine.State.SolutionPath}", text);
    }

    [Fact]
    public async Task Verify_ExpectedSolutionMismatch_Fails()
    {
        var engine = new StubEngine
        {
            NextVerification = new VerificationResult(VerificationVerdict.Passed, null, VerificationSource.Reused, 1, null, null),
        };
        var root = Path.Combine(Path.GetTempPath(), "wt-a");
        engine.State.SolutionPath = Path.Combine(root, "App.slnx");
        var tools = new TestTools(engine, NullMcpCallRecorder.Instance);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            tools.VerifyAsync(5, Path.Combine(Path.GetTempPath(), "wt-b"), CancellationToken.None));
        Assert.Contains("Solution mismatch", ex.Message);
        Assert.Null(engine.LastVerifyTimeout);

        // The worktree directory or the solution itself both match.
        Assert.Contains("Verdict: Passed", await tools.VerifyAsync(5, root, CancellationToken.None));
        Assert.Contains("Verdict: Passed", await tools.VerifyAsync(5, engine.State.SolutionPath, CancellationToken.None));
    }

    [Theory]
    [InlineData("/w/a/App.slnx", "/w/a/App.slnx", true)]
    [InlineData("/w/a/App.slnx", "/w/a", true)]
    [InlineData("/w/a/App.slnx", "/w/a/", true)]
    [InlineData("/w/a/src/App.slnx", "/w/a", true)]
    [InlineData("/w/a/App.slnx", "/w/ab", false)]
    [InlineData("/w/a/App.slnx", "/w/b/App.slnx", false)]
    [InlineData(null, "/w/a", false)]
    public void SolutionMatches(string? actual, string expected, bool matches)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix-style fixtures.
        }

        Assert.Equal(matches, TestTools.SolutionMatches(actual, expected));
    }
}
