// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Engine.Models;
using Piston.Hosting.Protocol;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Tests.Mcp;
using Xunit;

namespace Piston.Tests.Protocol;

public sealed class SessionTimelineCommandTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetTimeline_WithNoRuns_ReturnsAnEmptySession()
    {
        var dispatcher = new EngineCommandDispatcher(new StubEngine());

        var result = await dispatcher.HandleCommandAsync(ProtocolMethods.SessionGetTimeline, null, _ct);

        Assert.Empty(result!["entries"]!.AsArray());
        Assert.NotNull(result["startedAt"]);
    }

    [Fact]
    public async Task GetTimeline_ReturnsEntriesWithEditsOutcomesAndClassifications()
    {
        var engine = new StubEngine();
        engine.State.SolutionPath = "/repo/App.slnx";
        engine.State.SessionTimeline =
        [
            new SessionTimelineEntry(
                "r1", 2, TestRunTrigger.FileChange, TestRunStatus.Completed, null, TestRunScope.Selective, IsFullScope: false,
                IsFiltered: false, ResultFreshness.Current, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(4), "abc", "abc", SourceChangeObserved: false, ["/repo/Code.cs"], 1,
                BuildStatus.Succeeded, 0, Passed: 3, Failed: 1, Skipped: 0,
                [new TestOutcomeReference("A.Tests", "A.Broke")], 1, [new TestOutcomeReference("A.Tests", "A.Healed")], 1,
                [new FailureClassification("A.Tests", "A.Broke", FailureKind.New, "Passed in run r0.")], InRunHistory: true),
        ];
        var dispatcher = new EngineCommandDispatcher(engine);

        var node = await dispatcher.HandleCommandAsync(ProtocolMethods.SessionGetTimeline, null, _ct);
        var timeline = node.Deserialize<SessionTimelineDto>(JsonRpcSerializer.Options)!;

        Assert.Equal("/repo/App.slnx", timeline.SolutionPath);
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal("FileChange", entry.Trigger);
        Assert.Equal("Selective", entry.Scope);
        Assert.Equal("Current", entry.Freshness);
        Assert.Equal("Succeeded", entry.BuildStatus);
        Assert.Equal(["/repo/Code.cs"], entry.ChangedFiles);
        Assert.Equal(new TestOutcomeReferenceDto("A.Tests", "A.Broke"), Assert.Single(entry.NewlyFailingTests));
        Assert.Equal(new TestOutcomeReferenceDto("A.Tests", "A.Healed"), Assert.Single(entry.FixedTests));
        Assert.Equal(new FailureClassificationDto("A.Tests", "A.Broke", "New", "Passed in run r0."), Assert.Single(entry.FailureClassifications));
        Assert.True(entry.InRunHistory);
    }
}
