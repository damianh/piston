// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class TestOutcomeChangesTests
{
    private static TestResult Test(string name, TestStatus status, string? framework = null) =>
        new(name, name, status, TimeSpan.Zero, null, null, null, null) { TargetFramework = framework };

    private static IReadOnlyList<TestSuite> Suites(params TestResult[] tests) =>
        [new TestSuite("Tests", tests, DateTimeOffset.UnixEpoch, TimeSpan.Zero)];

    [Fact]
    public void ReportsFixedAndNewlyFailingTests()
    {
        var previous = Suites(
            Test("A.Fixed", TestStatus.Failed),
            Test("A.StillFailing", TestStatus.Failed),
            Test("A.Broke", TestStatus.Passed),
            Test("A.StillPassing", TestStatus.Passed),
            Test("A.NotRerun", TestStatus.Failed));
        var current = Suites(
            Test("A.Fixed", TestStatus.Passed),
            Test("A.StillFailing", TestStatus.Failed),
            Test("A.Broke", TestStatus.Failed),
            Test("A.StillPassing", TestStatus.Passed),
            Test("A.NewAndFailing", TestStatus.Failed),
            Test("A.NewAndPassing", TestStatus.Passed));

        var (fixedTests, newlyFailing) = TestOutcomeChanges.Compare(previous, current);

        Assert.Equal(["A.Fixed"], fixedTests);
        Assert.Equal(["A.Broke", "A.NewAndFailing"], newlyFailing);
    }

    [Fact]
    public void ReportsNothingWithoutEarlierResults()
    {
        var (fixedTests, newlyFailing) = TestOutcomeChanges.Compare([], Suites(Test("A.Fails", TestStatus.Failed)));

        Assert.Empty(fixedTests);
        Assert.Empty(newlyFailing);
    }

    [Fact]
    public void MatchesFrameworkResultsByKeyAndFallsBackToTheName()
    {
        var previous = Suites(
            Test("A.Multi", TestStatus.Failed, "net8.0"),
            Test("A.Multi", TestStatus.Passed, "net10.0"),
            Test("A.Became", TestStatus.Failed));
        var current = Suites(
            Test("A.Multi", TestStatus.Passed, "net8.0"),
            Test("A.Multi", TestStatus.Failed, "net10.0"),
            Test("A.Became", TestStatus.Passed));

        var (fixedTests, newlyFailing) = TestOutcomeChanges.Compare(previous, current);

        Assert.Equal(["A.Became", "A.Multi [net8.0]"], fixedTests);
        Assert.Equal(["A.Multi [net10.0]"], newlyFailing);
    }
}
