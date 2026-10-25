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
            DateTimeOffset.UtcNow, new ExecutionBudget(2, 1024, "/user/budget.json"),
            [], [], [], "Could not read worktree ownership", OwnersAvailable: false);

        var output = StatusAllFormatter.Format(overview);

        Assert.Contains("Worktree ownership is unknown.", output, StringComparison.Ordinal);
        Assert.Contains("Overview error: Could not read worktree ownership", output, StringComparison.Ordinal);
        Assert.DoesNotContain("No Piston process owns a worktree.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_UnavailableScanDoesNotReportIdleOrZeroUsage()
    {
        var now = DateTimeOffset.UtcNow;
        var owner = new OwnershipRecord(
            "worktree-key", OwnershipLiveness.Live,
            new ExecutionOwnerInfo(123, "/repo/App.slnx", "/repo", now, "daemon"));
        var overview = new MachineExecutionOverview(
            now, new ExecutionBudget(2, 1024, "/user/budget.json"),
            [owner], [], [], "Scan failed", CoordinationAvailable: false);

        var output = StatusAllFormatter.Format(overview);

        Assert.Contains("[Unknown]", output, StringComparison.Ordinal);
        Assert.Contains("CPU unknown/2", output, StringComparison.Ordinal);
        Assert.Contains("memory unknown/1,024 MiB", output, StringComparison.Ordinal);
        Assert.Contains("running and queued counts unknown", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[Idle]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ReportsOwnerStateBudgetAndFifoQueue()
    {
        var now = DateTimeOffset.UtcNow;
        var owner = new OwnershipRecord(
            "worktree-key",
            OwnershipLiveness.Live,
            new ExecutionOwnerInfo(123, "/repo/App.slnx", "/repo", now, "daemon", "http://localhost:5199"));
        var overview = new MachineExecutionOverview(
            now,
            new ExecutionBudget(2, 1024, "/user/budget.json"),
            [owner],
            [new MachineExecutionEntry(1, "running-id", 123, 1, 512, "worktree-key", null)],
            [new MachineExecutionEntry(2, "queued-id", 456, 1, 256, null, 1)],
            null);

        var output = StatusAllFormatter.Format(overview);

        Assert.Contains("[Running]", output, StringComparison.Ordinal);
        Assert.Contains("dashboard http://localhost:5199", output, StringComparison.Ordinal);
        Assert.Contains("CPU 1/2 slot(s); memory 512/1,024 MiB", output, StringComparison.Ordinal);
        Assert.Contains("Queue #1: PID 456 | run queued-id", output, StringComparison.Ordinal);
    }
}
