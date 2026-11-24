// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Piston.Hosting;
using Xunit;

namespace Piston.Tests;

public sealed class StatusAllFormatterTests
{
    [Fact]
    public void Format_UnavailableOwnersDoesNotReportNoWorktrees()
    {
        var overview = new MachineExecutionOverview(
            DateTimeOffset.UtcNow, [], "Could not read worktree ownership", OwnersAvailable: false);

        var output = StatusAllFormatter.Format(overview);

        Assert.Contains("Worktree ownership is unknown.", output, StringComparison.Ordinal);
        Assert.Contains("Overview error: Could not read worktree ownership", output, StringComparison.Ordinal);
        Assert.DoesNotContain("No Piston process owns a worktree.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ReportsLiveOwnerWithoutInferringRunState()
    {
        var now = DateTimeOffset.UtcNow;
        var owner = new OwnershipRecord(
            "worktree-key", OwnershipLiveness.Live,
            new ExecutionOwnerInfo(123, "/repo/App.slnx", "/repo", now, "daemon"));
        var overview = new MachineExecutionOverview(
            now, [owner], null);

        var output = StatusAllFormatter.Format(overview);

        Assert.Contains("Live :", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[Idle]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[Running]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Budget:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ReportsOwnerAndDashboard()
    {
        var now = DateTimeOffset.UtcNow;
        var owner = new OwnershipRecord(
            "worktree-key",
            OwnershipLiveness.Live,
            new ExecutionOwnerInfo(123, "/repo/App.slnx", "/repo", now, "daemon", "http://localhost:5199"));
        var overview = new MachineExecutionOverview(
            now,
            [owner],
            null);

        var output = StatusAllFormatter.Format(overview);

        Assert.Contains("Live :", output, StringComparison.Ordinal);
        Assert.Contains("dashboard http://localhost:5199", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Queue", output, StringComparison.Ordinal);
    }
}
