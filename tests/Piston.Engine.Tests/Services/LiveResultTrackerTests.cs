// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class LiveResultTrackerTests
{
    private static Dictionary<string, TestStatus> Results(LiveResultTracker tracker) =>
        tracker.Snapshot().SelectMany(s => s.Tests).ToDictionary(t => t.FullyQualifiedName, t => t.Status);

    [Fact]
    public void CollectorEvents_AreKeyedByQualifiedIdentity()
    {
        var tracker = new LiveResultTracker();

        _ = tracker.ApplyCollectorEvent(new LiveTestEvent("M", TestStatus.Running, "Ns.C.M"));

        var test = Assert.Single(tracker.Snapshot().SelectMany(s => s.Tests));
        Assert.Equal("Ns.C.M", test.FullyQualifiedName);
        Assert.Equal("M", test.DisplayName);
        Assert.Equal(TestStatus.Running, test.Status);
    }

    [Fact]
    public void IdenticalMethodNamesInDifferentClasses_AreTrackedSeparately()
    {
        var tracker = new LiveResultTracker();

        _ = tracker.ApplyCollectorEvent(new LiveTestEvent("M", TestStatus.Running, "Ns.C1.M"));
        _ = tracker.ApplyCollectorEvent(new LiveTestEvent("M", TestStatus.Running, "Ns.C2.M"));
        _ = tracker.ApplyCollectorEvent(new LiveTestEvent("M", TestStatus.Failed, "Ns.C1.M"));

        Assert.Equal(
            new Dictionary<string, TestStatus> { ["Ns.C1.M"] = TestStatus.Failed, ["Ns.C2.M"] = TestStatus.Running },
            Results(tracker));
    }

    [Fact]
    public void ConsoleResult_IsMappedToTheIdentityTheCollectorReported()
    {
        var tracker = new LiveResultTracker();

        _ = tracker.ApplyCollectorEvent(new LiveTestEvent("M", TestStatus.Running, "Ns.C.M"));
        _ = tracker.ApplyConsoleResult("M", TestStatus.Passed);
        // The collector's end event does not override the console result.
        _ = tracker.ApplyCollectorEvent(new LiveTestEvent("M", TestStatus.Failed, "Ns.C.M"));

        Assert.Equal(new Dictionary<string, TestStatus> { ["Ns.C.M"] = TestStatus.Passed }, Results(tracker));
    }

    [Fact]
    public void ConsoleResult_BeforeTheCollectorNamesTheTest_MovesToItsIdentity()
    {
        var tracker = new LiveResultTracker();

        _ = tracker.ApplyConsoleResult("M", TestStatus.Passed);
        _ = tracker.ApplyCollectorEvent(new LiveTestEvent("M", TestStatus.Running, "Ns.C.M"));

        Assert.Equal(new Dictionary<string, TestStatus> { ["Ns.C.M"] = TestStatus.Passed }, Results(tracker));
    }

    [Fact]
    public void ConsoleResult_ForAnAmbiguousDisplayName_IsLeftToTheCollector()
    {
        var tracker = new LiveResultTracker();
        _ = tracker.ApplyCollectorEvent(new LiveTestEvent("M", TestStatus.Running, "Ns.C1.M"));
        _ = tracker.ApplyCollectorEvent(new LiveTestEvent("M", TestStatus.Running, "Ns.C2.M"));

        Assert.False(tracker.ApplyConsoleResult("M", TestStatus.Passed));
        _ = tracker.ApplyCollectorEvent(new LiveTestEvent("M", TestStatus.Passed, "Ns.C2.M"));

        Assert.Equal(
            new Dictionary<string, TestStatus> { ["Ns.C1.M"] = TestStatus.Running, ["Ns.C2.M"] = TestStatus.Passed },
            Results(tracker));
    }

    [Fact]
    public void WithoutTheCollector_ConsoleResultsAreKeyedByDisplayName()
    {
        var tracker = new LiveResultTracker();

        _ = tracker.ApplyConsoleResult("Ns.C.M", TestStatus.Failed);

        Assert.Equal(new Dictionary<string, TestStatus> { ["Ns.C.M"] = TestStatus.Failed }, Results(tracker));
    }
}
