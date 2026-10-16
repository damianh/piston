// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Orchestration;
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
    public void FirstRun_DiscoveredTestsShowRunningOrQueuedByProjectStatus()
    {
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [],
            new Dictionary<string, IReadOnlyList<TestResult>>
            {
                [ProjA] = [Test("A.One"), Test("A.Two")],
                [ProjB] = [Test("B.One")],
            },
            new Dictionary<string, IReadOnlyList<TestSuite>>(),
            new Dictionary<string, ProjectRunStatus> { [ProjA] = ProjectRunStatus.Running, [ProjB] = ProjectRunStatus.Pending });

        Assert.Equal(["A.Tests", "B.Tests"], overlay.Select(s => s.Name));
        var status = StatusByFqn(overlay);
        Assert.Equal(TestStatus.Running, status["A.One"]);
        Assert.Equal(TestStatus.Running, status["A.Two"]);
        Assert.Equal(TestStatus.NotRun, status["B.One"]);
    }

    [Fact]
    public void LiveResultsReplaceSeededAndDiscoveredTests()
    {
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [Suite("A.Tests", Test("A.One", TestStatus.Failed), Test("A.Two", TestStatus.Passed))],
            new Dictionary<string, IReadOnlyList<TestResult>> { [ProjA] = [Test("A.One"), Test("A.New")] },
            new Dictionary<string, IReadOnlyList<TestSuite>> { [ProjA] = [Suite("A.Tests", Test("A.One", TestStatus.Passed))] },
            new Dictionary<string, ProjectRunStatus> { [ProjA] = ProjectRunStatus.Running });

        var suite = Assert.Single(overlay);
        Assert.Equal(["A.One", "A.Two", "A.New"], suite.Tests.Select(t => t.FullyQualifiedName));
        var status = StatusByFqn(overlay);
        Assert.Equal(TestStatus.Passed, status["A.One"]);
        Assert.Equal(TestStatus.Running, status["A.Two"]);
        Assert.Equal(TestStatus.Running, status["A.New"]);
    }

    [Fact]
    public void CompletedProject_TestsWithoutResultsAreNotRun()
    {
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [Suite("A.Tests", Test("A.One", TestStatus.Passed), Test("A.Removed", TestStatus.Passed))],
            new Dictionary<string, IReadOnlyList<TestResult>>(),
            new Dictionary<string, IReadOnlyList<TestSuite>> { [ProjA] = [Suite("A.Tests", Test("A.One", TestStatus.Passed))] },
            new Dictionary<string, ProjectRunStatus> { [ProjA] = ProjectRunStatus.Completed });

        var status = StatusByFqn(overlay);
        Assert.Equal(TestStatus.Passed, status["A.One"]);
        Assert.Equal(TestStatus.NotRun, status["A.Removed"]);
    }

    [Fact]
    public void UnattributedSuite_IsShownAsRunning_AndLiveTestsFromSolutionRunKeepTheirSuite()
    {
        var overlay = PistonOrchestrator.ComposeTestOverlay(
            [Suite("Some.Namespace", Test("Some.Namespace.T1", TestStatus.Passed))],
            new Dictionary<string, IReadOnlyList<TestResult>>(),
            new Dictionary<string, IReadOnlyList<TestSuite>> { ["/src/All.slnx"] = [Suite("Live", Test("X.New", TestStatus.Failed))] },
            new Dictionary<string, ProjectRunStatus>());

        Assert.Equal(["Some.Namespace", "Live"], overlay.Select(s => s.Name));
        var status = StatusByFqn(overlay);
        Assert.Equal(TestStatus.Running, status["Some.Namespace.T1"]);
        Assert.Equal(TestStatus.Failed, status["X.New"]);
    }
}
