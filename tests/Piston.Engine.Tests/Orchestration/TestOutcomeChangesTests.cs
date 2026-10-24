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

        Assert.Equal([new("Tests", "A.Fixed")], fixedTests);
        Assert.Equal([new("Tests", "A.Broke"), new("Tests", "A.NewAndFailing")], newlyFailing);
    }

    [Fact]
    public void ReportsNothingWithoutEarlierResults()
    {
        var (fixedTests, newlyFailing) = TestOutcomeChanges.Compare([], Suites(Test("A.Fails", TestStatus.Failed)));

        Assert.Empty(fixedTests);
        Assert.Empty(newlyFailing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("net10.0")]
    public void DoesNotMixOutcomesFromDifferentProjects(string? framework)
    {
        IReadOnlyList<TestSuite> previous =
        [
            new("A.Tests", [Test("Shared.Test", TestStatus.Passed, framework)], DateTimeOffset.UnixEpoch, TimeSpan.Zero),
            new("B.Tests", [Test("Shared.Test", TestStatus.Failed, "net10.0")], DateTimeOffset.UnixEpoch, TimeSpan.Zero),
        ];
        IReadOnlyList<TestSuite> current =
        [
            previous[0],
            previous[1] with { Tests = [Test("Shared.Test", TestStatus.Failed, framework)] },
        ];

        var (fixedTests, newlyFailing) = TestOutcomeChanges.Compare(previous, current);

        Assert.Empty(fixedTests);
        Assert.Empty(newlyFailing);
    }

    [Fact]
    public void FailureInAnotherProjectDoesNotHideANewFailure()
    {
        var previous = Suites(Test("Shared.Test", TestStatus.Failed));
        var current = new[] { previous[0] with { Name = "Other.Tests" } };

        var (_, newlyFailing) = TestOutcomeChanges.Compare(previous, current);

        Assert.Equal([new("Other.Tests", "Shared.Test")], newlyFailing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreservesSameNamedFixesAndOppositeTransitionsInDifferentProjects(bool opposite)
    {
        IReadOnlyList<TestSuite> previous =
        [
            new("A.Tests", [Test("Shared.Test", TestStatus.Failed)], DateTimeOffset.UnixEpoch, TimeSpan.Zero),
            new("B.Tests", [Test("Shared.Test", opposite ? TestStatus.Passed : TestStatus.Failed)], DateTimeOffset.UnixEpoch, TimeSpan.Zero),
        ];
        IReadOnlyList<TestSuite> current =
        [
            previous[0] with { Tests = [Test("Shared.Test", TestStatus.Passed)] },
            previous[1] with { Tests = [Test("Shared.Test", opposite ? TestStatus.Failed : TestStatus.Passed)] },
        ];

        var (fixedTests, newlyFailing) = TestOutcomeChanges.Compare(previous, current);

        Assert.Contains(new("A.Tests", "Shared.Test"), fixedTests);
        if (opposite)
        {
            _ = Assert.Single(fixedTests);
            Assert.Equal([new("B.Tests", "Shared.Test")], newlyFailing);
        }
        else
        {
            Assert.Equal([new("A.Tests", "Shared.Test"), new("B.Tests", "Shared.Test")], fixedTests);
            Assert.Empty(newlyFailing);
        }
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

        Assert.Equal([new("Tests", "A.Became"), new("Tests", "A.Multi [net8.0]")], fixedTests);
        Assert.Equal([new("Tests", "A.Multi [net10.0]")], newlyFailing);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("net10.0", false)]
    [InlineData("net10.0", true)]
    public void ConflictingCurrentResultsDoNotReportAPreviousFailureAsFixed(string? framework, bool failureFirst)
    {
        var previous = Suites(Test("A.StillFailing", TestStatus.Failed, framework));
        var passed = Test("A.StillFailing", TestStatus.Passed, framework);
        var failed = Test("A.StillFailing", TestStatus.Failed, framework);
        var current = failureFirst ? Suites(failed, passed) : Suites(passed, failed);

        var (fixedTests, newlyFailing) = TestOutcomeChanges.Compare(previous, current);

        Assert.Empty(fixedTests);
        Assert.Empty(newlyFailing);
    }
}
