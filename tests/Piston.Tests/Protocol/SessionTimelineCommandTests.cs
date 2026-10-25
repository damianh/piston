// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Engine;
using Piston.Engine.Models;
using Piston.Hosting.Mapping;
using Piston.Hosting.Protocol;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Piston.Tests.Mcp;
using Xunit;

namespace Piston.Tests.Protocol;

public sealed class SessionTimelineCommandTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static SessionTimelineEntry Entry(string id, long sequence, int newlyFailing = 0, string keyPrefix = "A.Broke") =>
        new(
            id, sequence, TestRunTrigger.Forced, TestRunStatus.Completed, null, TestRunScope.Full, IsFullScope: true,
            IsFiltered: false, ResultFreshness.Current, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, "abc", "abc", SourceChangeObserved: false, [], 0, BuildStatus.Succeeded, 0,
            Passed: 1, Failed: newlyFailing, Skipped: 0,
            [.. Enumerable.Range(0, newlyFailing).Select(i => new TestOutcomeReference("A.Tests", $"{keyPrefix}{i}"))], newlyFailing,
            [], 0, [], InRunHistory: true);

    private static StoredRun Stored(string id) => new(
        new StoredRunSummary(id, TestRunTrigger.Forced, TestRunStatus.Completed, TestRunScope.Full, IsFullScope: true,
            ResultFreshness.Current, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "abc", 1, 0, 0),
        []);

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
        engine.StoredRuns = [Stored("r1")];
        var dispatcher = new EngineCommandDispatcher(engine);

        var node = await dispatcher.HandleCommandAsync(ProtocolMethods.SessionGetTimeline, null, _ct);
        var timeline = node.Deserialize<SessionTimelineDto>(JsonRpcSerializer.Options)!;

        Assert.Equal("/repo/App.slnx", timeline.SolutionPath);
        Assert.False(timeline.HasMore);
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

    [Fact]
    public async Task GetTimeline_ReportsRunsSinceEvictedFromRunHistoryAsNotInIt()
    {
        var engine = new StubEngine { StoredRuns = [Stored("r2")] };
        engine.State.SessionTimeline = [Entry("r1", 1), Entry("r2", 2)];
        var dispatcher = new EngineCommandDispatcher(engine);

        var node = await dispatcher.HandleCommandAsync(ProtocolMethods.SessionGetTimeline, null, _ct);
        var timeline = node.Deserialize<SessionTimelineDto>(JsonRpcSerializer.Options)!;

        Assert.Equal([("r1", false), ("r2", true)], timeline.Entries.Select(e => (e.RunId, e.InRunHistory)));
    }

    [Fact]
    public async Task GetTimeline_AfterASequence_ReturnsLaterRunsInRequestOrderWithTheDroppedCount()
    {
        var engine = new StubEngine();
        engine.State.SessionTimeline = [Entry("r1", 1), Entry("r3", 3), Entry("r2", 2)];
        engine.State.SessionTimelineDroppedRuns = 7;
        var dispatcher = new EngineCommandDispatcher(engine);

        var node = await dispatcher.HandleCommandAsync(
            ProtocolMethods.SessionGetTimeline, JsonSerializer.SerializeToNode(new GetSessionTimelineCommand(1), JsonRpcSerializer.Options), _ct);
        var timeline = node.Deserialize<SessionTimelineDto>(JsonRpcSerializer.Options)!;

        Assert.Equal(["r2", "r3"], timeline.Entries.Select(e => e.RunId));
        Assert.Equal(7, timeline.EarlierRunsDropped);
    }

    [Fact]
    public void ToSessionTimelineDto_SplitsEntriesIntoPagesWithinTheBudget()
    {
        var state = new PistonState { SessionTimeline = [.. Enumerable.Range(1, 10).Select(i => Entry($"r{i}", i, newlyFailing: 5))] };
        var entrySize = JsonSerializer.SerializeToUtf8Bytes(state.SessionTimeline[0].ToDto(), JsonRpcSerializer.Options).Length;
        var budget = (entrySize * 3) + (entrySize / 2);

        var pages = new List<SessionTimelineDto>();
        long? after = null;
        do
        {
            pages.Add(state.ToSessionTimelineDto(after, _ => true, budget));
            after = pages[^1].Entries[^1].Sequence;
        }
        while (pages[^1].HasMore);

        Assert.Equal([3, 3, 3, 1], pages.Select(p => p.Entries.Count));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"r{i}"), pages.SelectMany(p => p.Entries).Select(e => e.RunId));
    }

    [Fact]
    public void ToSessionTimelineDto_TrimsTheListsOfAnEntryLargerThanTheBudgetButKeepsItsTotals()
    {
        var state = new PistonState { SessionTimeline = [Entry("r1", 1, newlyFailing: 200, keyPrefix: new string('x', 500))] };

        var page = state.ToSessionTimelineDto(null, _ => true, budget: 20_000);

        var entry = Assert.Single(page.Entries);
        Assert.InRange(entry.NewlyFailingTests.Count, 1, 199);
        Assert.Equal(200, entry.NewlyFailingCount);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(entry, JsonRpcSerializer.Options).Length, 0, 20_000);
        Assert.False(page.HasMore);
    }
}
