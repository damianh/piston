// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Piston.Hosting;
using Xunit;

namespace Piston.Tests;

public sealed class StatusAllFormatterTests
{
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
