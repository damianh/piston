// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Engine;
using Piston.Engine.Execution;
using Piston.Engine.Models;
using Piston.Engine.Verification;
using Piston.Hosting.Mapping;
using Piston.Mcp.Tools;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Xunit;

namespace Piston.Tests.Mcp;

public sealed class TestResourceReportingTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly SourceFingerprint Source = new("piston-source-v2", new string('a', 64), 3);

    private static TestResourceWait Waiting(params TestResourceBlocker[] blockers) => new(
        TestResourceWaitState.Waiting, ["port:5432", "project:repo/tests/Db.Tests.csproj"], true, blockers, 1, 2, 2, T0, null);

    private static TestResourceBlocker Held(string resource) =>
        new(resource, TestResourceBlockerKind.Held, 42, "/wt/other", "/wt/other/tests/Db.Tests.csproj", T0);

    private static TestProjectResources Acquired(TimeSpan waited, params string[] warnings) => new(
        new TestResourceNeeds("/w/tests/Db.Tests.csproj", ["port:5432"], true, ["references Testcontainers"]),
        new TestResourceWait(TestResourceWaitState.Acquired, ["port:5432"], true, [], 0, 1, 2, T0, T0 + waited),
        warnings);

    private static TestRunRecord Run(params TestRunProjectOutcome[] projects) => new()
    {
        RunId = "run-9",
        EngineInstanceId = "engine",
        Sequence = 9,
        Trigger = TestRunTrigger.Forced,
        Status = TestRunStatus.Completed,
        Scope = TestRunScope.Full,
        Freshness = ResultFreshness.Current,
        RequestedAt = T0,
        StartedAt = T0,
        CompletedAt = T0,
        SourceAfter = Source,
        Projects = projects,
    };

    private static TestRunProjectOutcome Outcome(string path, TestProjectResources? resources = null, string? conflict = null) =>
        new(path, ProjectRunStatus.Completed, 0) { Resources = resources, SuspectedResourceConflict = conflict };

    [Fact]
    public void DescribeResourceWait_NamesEachBlocker()
    {
        var wait = Waiting(
            Held("port:5432"),
            new("project:repo/tests/Db.Tests.csproj", TestResourceBlockerKind.Queued, 7, "/wt/third", "/wt/third/tests/Db.Tests.csproj", T0),
            Held(TestResourceBlocker.ContainerSlots));

        var text = TestTools.DescribeResourceWait("/w/tests/Db.Tests.csproj", wait, T0.AddSeconds(12));

        Assert.Equal(
            "Db.Tests waiting 12 s for test resources: port:5432 held by Db.Tests in /wt/other (pid 42); " +
            "project:repo/tests/Db.Tests.csproj queued ahead for Db.Tests in /wt/third (pid 7); " +
            "container slots (2/2 in use) held by Db.Tests in /wt/other (pid 42)",
            text);
    }

    [Fact]
    public void DescribeResourceNotes_ReportsLongWaitsWarningsAndConflicts_Only()
    {
        var run = Run(
            Outcome("/w/tests/Db.Tests.csproj", Acquired(TimeSpan.FromSeconds(3), "port 5432 is in use outside Piston")),
            Outcome("/w/tests/Quick.Tests.csproj", Acquired(TimeSpan.FromMilliseconds(200))),
            Outcome("/w/tests/Api.Tests.csproj", conflict: "failed with 'address already in use'"),
            Outcome("/w/tests/Plain.Tests.csproj"));

        var notes = TestTools.DescribeResourceNotes(run, TestTools.ResourceWaitWorthReporting);

        Assert.Equal(
            [
                "Db.Tests waited 3 s for port:5432, a container slot",
                "Db.Tests: port 5432 is in use outside Piston",
                "Api.Tests: failed with 'address already in use'",
            ],
            notes);
    }

    [Fact]
    public void GetTestResults_ReportsCurrentWaitsAndLastRunNotes()
    {
        var engine = new StubEngine();
        engine.State.ProjectResourceWaits = new Dictionary<string, TestResourceWait>
        {
            ["/w/tests/Db.Tests.csproj"] = Waiting(Held("port:5432")),
        };
        engine.State.LastRun = Run(Outcome("/w/tests/Db.Tests.csproj", Acquired(TimeSpan.FromSeconds(3))));

        var text = new TestTools(engine, NullMcpCallRecorder.Instance).GetTestResults();

        Assert.Contains("Db.Tests waiting ", text);
        Assert.Contains("for test resources: port:5432 held by Db.Tests in /wt/other (pid 42)", text);
        Assert.Contains("Test resources: Db.Tests waited 3 s for port:5432, a container slot", text);
    }

    [Fact]
    public void GetTestRun_IncludesProjectResources()
    {
        var engine = new StubEngine();
        engine.State.LastRun = Run(
            Outcome("/w/tests/Db.Tests.csproj", Acquired(TimeSpan.FromSeconds(3), "warned")),
            Outcome("/w/tests/Plain.Tests.csproj"));

        using var doc = JsonDocument.Parse(new TestTools(engine, NullMcpCallRecorder.Instance).GetTestRun("latest"));

        var projects = doc.RootElement.GetProperty("projects");
        var resources = projects[0].GetProperty("resources");
        Assert.Equal("port:5432", resources.GetProperty("keys")[0].GetString());
        Assert.True(resources.GetProperty("usesContainers").GetBoolean());
        Assert.Equal("references Testcontainers", resources.GetProperty("reasons")[0].GetString());
        Assert.Equal("warned", resources.GetProperty("warnings")[0].GetString());
        var wait = resources.GetProperty("wait");
        Assert.Equal("Acquired", wait.GetProperty("state").GetString());
        Assert.Equal(3000, wait.GetProperty("waitedMs").GetDouble());
        Assert.Equal(2, wait.GetProperty("containerSlotBudget").GetInt32());
        Assert.True(!projects[1].TryGetProperty("resources", out var none) || none.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Verify_ReportsResourceNotes()
    {
        var run = Run(Outcome("/w/tests/Db.Tests.csproj", Acquired(TimeSpan.FromSeconds(5))));
        var engine = new StubEngine
        {
            NextVerification = new(VerificationVerdict.Passed, null, VerificationSource.Forced, 1, run, Source),
        };

        var text = await new TestTools(engine, NullMcpCallRecorder.Instance).VerifyAsync(600, ct: CancellationToken.None);

        Assert.Contains("Test resources (1):", text);
        Assert.Contains("  - Db.Tests waited 5 s for port:5432, a container slot", text);
    }

    [Fact]
    public void Protocol_CarriesRunResourcesAndLiveWaits()
    {
        var run = Run(
            Outcome("/w/tests/Db.Tests.csproj", Acquired(TimeSpan.FromSeconds(3))),
            Outcome("/w/tests/Plain.Tests.csproj"),
            Outcome("/w/tests/Api.Tests.csproj", conflict: "looks like a port conflict"));
        var state = new PistonState
        {
            ProjectStatuses = new Dictionary<string, ProjectRunStatus> { ["/w/tests/Db.Tests.csproj"] = ProjectRunStatus.WaitingForResources },
            ProjectResourceWaits = new Dictionary<string, TestResourceWait> { ["/w/tests/Db.Tests.csproj"] = Waiting(Held("port:5432")) },
        };

        var runDto = run.ToDto();
        var snapshot = JsonSerializer.Deserialize<StateSnapshotNotification>(
            JsonSerializer.SerializeToUtf8Bytes(state.ToSnapshot(), JsonRpcSerializer.Options), JsonRpcSerializer.Options)!;
        var json = JsonSerializer.Serialize(state.ToSnapshot(), JsonRpcSerializer.Options);

        Assert.Equal(["/w/tests/Db.Tests.csproj", "/w/tests/Api.Tests.csproj"], runDto.ProjectResources!.Select(p => p.ProjectPath));
        Assert.Equal("Acquired", runDto.ProjectResources![0].Wait!.State);
        Assert.Equal(3000, runDto.ProjectResources[0].Wait!.WaitedMs);
        Assert.Equal("looks like a port conflict", runDto.ProjectResources[1].SuspectedConflict);
        Assert.Null(Run(Outcome("/w/tests/Plain.Tests.csproj")).ToDto().ProjectResources);

        Assert.Contains("\"waitingForResources\"", json);
        Assert.Contains("\"projectResourceWaits\"", json);
        var wait = snapshot.ProjectResourceWaits!["/w/tests/Db.Tests.csproj"];
        Assert.Equal("Waiting", wait.State);
        var blocker = Assert.Single(wait.BlockedBy);
        Assert.Equal(("port:5432", "Held", 42, "/wt/other"), (blocker.Resource, blocker.Kind, blocker.ProcessId, blocker.Worktree));
        Assert.Null(new PistonState().ToSnapshot().ProjectResourceWaits);
    }
}
