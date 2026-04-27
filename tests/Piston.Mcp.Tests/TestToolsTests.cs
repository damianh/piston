using Piston.Engine.Models;
using Piston.Mcp.Tools;
using Xunit;

namespace Piston.Mcp.Tests;

public sealed class TestToolsTests
{
    [Fact]
    public async Task RunTestsReturnsResultSummary()
    {
        var engine = new StubEngine();
        engine.State.Phase = PistonPhase.Idle;
        engine.State.CompletedTests = 5;
        engine.State.TotalExpectedTests = 5;
        engine.State.TestSuites =
        [
            new TestSuite("Suite1",
            [
                new TestResult("Test1", "Test1", TestStatus.Passed, TimeSpan.FromMilliseconds(100), null, null, null, null),
                new TestResult("Test2", "Test2", TestStatus.Passed, TimeSpan.FromMilliseconds(200), null, null, null, null),
            ], DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(300)),
        ];

        var result = await TestTools.RunTests(engine, CancellationToken.None);

        Assert.True(engine.ForceRunCalled);
        Assert.Contains("Phase: Idle", result);
        Assert.Contains("Passed: 2", result);
        Assert.Contains("Suites: 1", result);
    }

    [Fact]
    public void GetTestResultsReturnsFormattedOutput()
    {
        var engine = new StubEngine();
        engine.State.Phase = PistonPhase.Idle;
        engine.State.CompletedTests = 3;
        engine.State.TotalExpectedTests = 3;
        engine.State.TestSuites =
        [
            new TestSuite("Suite1",
            [
                new TestResult("NS.Test1", "Test1", TestStatus.Passed, TimeSpan.FromMilliseconds(50), null, null, null, null),
                new TestResult("NS.Test2", "Test2", TestStatus.Failed, TimeSpan.FromMilliseconds(100), null, "Assert failed", "at line 5", null),
                new TestResult("NS.Test3", "Test3", TestStatus.Skipped, TimeSpan.Zero, null, null, null, null),
            ], DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(150)),
        ];

        var result = TestTools.GetTestResults(engine);

        Assert.Contains("Phase: Idle", result);
        Assert.Contains("Passed: 1", result);
        Assert.Contains("Failed: 1", result);
        Assert.Contains("Skipped: 1", result);
        Assert.Contains("Suite: Suite1", result);
        Assert.Contains("[Passed] Test1", result);
        Assert.Contains("[Failed] Test2", result);
        Assert.Contains("Assert failed", result);
        Assert.Contains("[Skipped] Test3", result);
        Assert.Contains("50ms", result);
    }

    [Fact]
    public void GetTestResultsWithNoSuitesReturnsEmpty()
    {
        var engine = new StubEngine();
        engine.State.Phase = PistonPhase.Idle;

        var result = TestTools.GetTestResults(engine);

        Assert.Contains("Phase: Idle", result);
        Assert.Contains("Tests: 0/0", result);
    }

    [Fact]
    public void SetTestFilterSetsFilter()
    {
        var engine = new StubEngine();

        var result = TestTools.SetTestFilter(engine, "MyTest");

        Assert.Equal("MyTest", engine.LastFilter);
        Assert.Contains("MyTest", result);
    }

    [Fact]
    public void SetTestFilterEmptyClearsFilter()
    {
        var engine = new StubEngine();

        var result = TestTools.SetTestFilter(engine, "");

        Assert.Null(engine.LastFilter);
        Assert.Contains("(none)", result);
    }

    [Fact]
    public void ClearResultsCallsEngine()
    {
        var engine = new StubEngine();

        var result = TestTools.ClearResults(engine);

        Assert.True(engine.ClearResultsCalled);
        Assert.Contains("Results cleared", result);
    }
}
