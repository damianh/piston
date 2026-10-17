// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class TestOverlayCompositionTests
{
    private const string ProjA = "/src/A.Tests/A.Tests.csproj";
    private const string ProjB = "/src/B.Tests/B.Tests.csproj";

    private static TestResult Test(string fqn, TestStatus status = TestStatus.NotRun) =>
        new(fqn, fqn, status, TimeSpan.Zero, null, null, null, null);

    private static TestSuite Suite(string name, params TestResult[] tests) =>
        new(name, tests, DateTimeOffset.UnixEpoch, TimeSpan.Zero);

    private static Dictionary<string, TestStatus> StatusByFqn(IReadOnlyList<TestSuite> suites) =>
        suites.SelectMany(s => s.Tests).ToDictionary(t => t.FullyQualifiedName, t => t.Status);

    [Fact]
    public void FirstRun_DiscoveredTestsAreQueuedUntilTheRunnerReportsThem()
    {
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [],
            new Dictionary<string, IReadOnlyList<TestResult>>
            {
                [ProjA] = [Test("A.One"), Test("A.Two")],
                [ProjB] = [Test("B.One")],
            },
            new Dictionary<string, IReadOnlyList<TestSuite>> { [ProjA] = [Suite("A.Tests", Test("A.One", TestStatus.Running))] },
            filtered: false);

        Assert.Equal(["A.Tests", "B.Tests"], overlay.Select(s => s.Name));
        var status = StatusByFqn(overlay);
        Assert.Equal(TestStatus.Running, status["A.One"]);
        Assert.Equal(TestStatus.NotRun, status["A.Two"]);
        Assert.Equal(TestStatus.NotRun, status["B.One"]);
    }

    [Fact]
    public void LiveResultsReplaceSeededAndDiscoveredTests()
    {
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [Suite("A.Tests", Test("A.One", TestStatus.Failed), Test("A.Two", TestStatus.Passed))],
            new Dictionary<string, IReadOnlyList<TestResult>> { [ProjA] = [Test("A.One"), Test("A.New")] },
            new Dictionary<string, IReadOnlyList<TestSuite>> { [ProjA] = [Suite("A.Tests", Test("A.One", TestStatus.Passed))] },
            filtered: false);

        var suite = Assert.Single(overlay);
        Assert.Equal(["A.One", "A.Two", "A.New"], suite.Tests.Select(t => t.FullyQualifiedName));
        var status = StatusByFqn(overlay);
        Assert.Equal(TestStatus.Passed, status["A.One"]);
        Assert.Equal(TestStatus.NotRun, status["A.Two"]);
        Assert.Equal(TestStatus.NotRun, status["A.New"]);
    }

    [Fact]
    public void FilteredRun_KnownTestsOutsideTheFilterKeepTheirLastStatus()
    {
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [Suite("A.Tests", Test("A.One", TestStatus.Failed), Test("A.Two", TestStatus.Passed), Test("A.Three", TestStatus.Failed))],
            new Dictionary<string, IReadOnlyList<TestResult>> { [ProjA] = [Test("A.One"), Test("A.Two")] },
            new Dictionary<string, IReadOnlyList<TestSuite>> { [ProjA] = [Suite("A.Tests", Test("A.One", TestStatus.Running))] },
            filtered: true);

        var status = StatusByFqn(overlay);
        Assert.Equal(TestStatus.Running, status["A.One"]);
        Assert.Equal(TestStatus.NotRun, status["A.Two"]);
        Assert.Equal(TestStatus.Failed, status["A.Three"]);
    }

    [Fact]
    public void UnfilteredRun_TestsWithoutLiveResultsAreNotRun()
    {
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [Suite("A.Tests", Test("A.One", TestStatus.Passed), Test("A.Removed", TestStatus.Passed))],
            new Dictionary<string, IReadOnlyList<TestResult>>(),
            new Dictionary<string, IReadOnlyList<TestSuite>> { [ProjA] = [Suite("A.Tests", Test("A.One", TestStatus.Passed))] },
            filtered: false);

        var status = StatusByFqn(overlay);
        Assert.Equal(TestStatus.Passed, status["A.One"]);
        Assert.Equal(TestStatus.NotRun, status["A.Removed"]);
    }

    [Fact]
    public void LiveTestsFromSolutionRunKeepTheirSuite()
    {
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [Suite("Some.Namespace", Test("Some.Namespace.T1", TestStatus.Passed))],
            new Dictionary<string, IReadOnlyList<TestResult>>(),
            new Dictionary<string, IReadOnlyList<TestSuite>> { ["/src/All.slnx"] = [Suite("Live", Test("X.New", TestStatus.Failed))] },
            filtered: false);

        Assert.Equal(["Some.Namespace", "Live"], overlay.Select(s => s.Name));
        var status = StatusByFqn(overlay);
        Assert.Equal(TestStatus.NotRun, status["Some.Namespace.T1"]);
        Assert.Equal(TestStatus.Failed, status["X.New"]);
    }

    [Fact]
    public void Rerun_LiveResultWithUnqualifiedDisplayNameReplacesTheSeededTest()
    {
        // sample-mixed.trx style: display name M, TRX identity Ns.C.M; the collector reports both names.
        var live = LiveResult("M", TestStatus.Running, "Ns.C.M");
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [Suite("A.Tests", Test("Ns.C.M", TestStatus.Passed), Test("Ns.C.N", TestStatus.Passed))],
            new Dictionary<string, IReadOnlyList<TestResult>>(),
            new Dictionary<string, IReadOnlyList<TestSuite>> { [ProjA] = [Suite("…", live)] },
            filtered: false,
            out var expected,
            out var completed);

        var status = StatusByFqn(overlay);
        Assert.Equal(new Dictionary<string, TestStatus> { ["Ns.C.M"] = TestStatus.Running, ["Ns.C.N"] = TestStatus.NotRun }, status);
        Assert.Equal(2, expected);
        Assert.Equal(0, completed);
    }

    [Fact]
    public void UnqualifiedDiscoveredNames_StandForTheProjectsKnownTests()
    {
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [],
            new Dictionary<string, IReadOnlyList<TestResult>>
            {
                [ProjA] = [Test("M"), Test("Other")],
                [ProjB] = [Test("M")],
            },
            new Dictionary<string, IReadOnlyList<TestSuite>>
            {
                [ProjA] = [Suite("…", LiveResult("M", TestStatus.Passed, "Ns.C1.M"), LiveResult("M", TestStatus.Running, "Ns.C2.M"))],
            },
            filtered: false,
            out var expected,
            out var completed);

        // Project A's "M" is covered by both live tests; project B's "M" is a different test and stays queued.
        Assert.Equal(["Other", "Ns.C1.M", "Ns.C2.M"], overlay.Single(s => s.Name == "A.Tests").Tests.Select(t => t.FullyQualifiedName));
        Assert.Equal(["M"], overlay.Single(s => s.Name == "B.Tests").Tests.Select(t => t.FullyQualifiedName));
        Assert.Equal(4, expected);
        Assert.Equal(1, completed);
    }

    [Fact]
    public void FilteredRun_UnqualifiedDiscoveredNameSelectsTheSeededTest()
    {
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [Suite("A.Tests", Test("Ns.C1.M", TestStatus.Failed), Test("Ns.C1.N", TestStatus.Passed))],
            new Dictionary<string, IReadOnlyList<TestResult>> { [ProjA] = [Test("M")] },
            new Dictionary<string, IReadOnlyList<TestSuite>>(),
            filtered: true,
            out var expected,
            out var completed);

        var status = StatusByFqn(overlay);
        Assert.Equal(new Dictionary<string, TestStatus> { ["Ns.C1.M"] = TestStatus.NotRun, ["Ns.C1.N"] = TestStatus.Passed }, status);
        Assert.Equal(1, expected);
        Assert.Equal(0, completed);
    }

    [Fact]
    public void FilteredRun_KnownResultsOutsideTheRunAreNotCountedAsProgress()
    {
        // Before discovery reports which tests the filter selects, nothing has run yet.
        _ = PistonOrchestrator.ComposeTestOverlay(
            [Suite("A.Tests", Test("A.One", TestStatus.Passed), Test("A.Two", TestStatus.Passed))],
            new Dictionary<string, IReadOnlyList<TestResult>>(),
            new Dictionary<string, IReadOnlyList<TestSuite>>(),
            filtered: true,
            out var expected,
            out var completed);

        Assert.Equal(0, expected);
        Assert.Equal(0, completed);

        _ = PistonOrchestrator.ComposeTestOverlay(
            [Suite("A.Tests", Test("A.One", TestStatus.Passed), Test("A.Two", TestStatus.Passed))],
            new Dictionary<string, IReadOnlyList<TestResult>> { [ProjA] = [Test("A.One")] },
            new Dictionary<string, IReadOnlyList<TestSuite>> { [ProjA] = [Suite("A.Tests", Test("A.One", TestStatus.Failed))] },
            filtered: true,
            out expected,
            out completed);

        Assert.Equal(1, expected);
        Assert.Equal(1, completed);
    }

    private static TestResult LiveResult(string displayName, TestStatus status, string fqn)
    {
        var tracker = new LiveResultTracker();
        _ = tracker.ApplyCollectorEvent(new LiveTestEvent(displayName, status, fqn));
        return tracker.Snapshot().Single().Tests.Single();
    }

    private static TestResult Test(string fqn, string framework, TestStatus status) =>
        Test(fqn, status) with { TargetFramework = framework };

    [Fact]
    public void MultiTargeted_LiveResultWithoutFramework_StandsForEveryFrameworkRow()
    {
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [Suite("A.Tests", Test("A.One", "net8.0", TestStatus.Passed), Test("A.One", "net10.0", TestStatus.Failed))],
            new Dictionary<string, IReadOnlyList<TestResult>> { [ProjA] = [Test("A.One")] },
            new Dictionary<string, IReadOnlyList<TestSuite>> { [ProjA] = [Suite("A.Tests", Test("A.One", TestStatus.Running))] },
            filtered: false);

        var tests = Assert.Single(overlay).Tests;
        Assert.Equal(["A.One [net8.0]", "A.One [net10.0]"], tests.Select(t => t.ResultKey));
        Assert.All(tests, t => Assert.Equal(TestStatus.Running, t.Status));
    }

    [Fact]
    public void MultiTargeted_FrameworkResultsReplaceTheDiscoveredPlaceholder()
    {
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [],
            new Dictionary<string, IReadOnlyList<TestResult>> { [ProjA] = [Test("A.One"), Test("A.Two")] },
            new Dictionary<string, IReadOnlyList<TestSuite>>
            {
                [ProjA] = [Suite("A.Tests", Test("A.One", "net8.0", TestStatus.Passed), Test("A.One", "net10.0", TestStatus.Failed))],
            },
            filtered: false);

        var tests = Assert.Single(overlay).Tests;
        Assert.Equal(["A.Two", "A.One [net8.0]", "A.One [net10.0]"], tests.Select(t => t.ResultKey));
        Assert.Equal(TestStatus.NotRun, tests[0].Status);
        Assert.Equal(TestStatus.Failed, tests[2].Status);
    }
}
