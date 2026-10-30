// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Impact;
using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.Coverage;

public sealed class ImpactBackfillTests
{
    private const string Project = "/src/A.Tests/A.Tests.csproj";

    private static readonly TestSuite Suite = new("A.Tests",
    [
        Test("A.Tests.Foo.One"),
        Test("A.Tests.Foo.Two(x: 1)", method: "A.Tests.Foo.Two"),
        Test("A.Tests.Foo.Two(x: 2)", method: "A.Tests.Foo.Two"),
        Test("Adds numbers", method: "A.Tests.Outer+Inner.Adds"),
    ], DateTimeOffset.UtcNow, TimeSpan.Zero);

    private static TestResult Test(string fqn, string? method = null) =>
        new(fqn, fqn, TestStatus.Passed, TimeSpan.Zero, null, null, null, null) { Method = method };

    private static ImpactUnitInfo Recorded(string key, bool current, bool hasBranches = true, params string[] tests) =>
        new(key, 1, current, DateTimeOffset.UtcNow, tests, hasBranches);

    [Fact]
    public void Create_PlansEveryClassWithAFilterRunningJustIt()
    {
        var plan = ImpactBackfill.Create([Project], [Suite], _ => [], ImpactGranularity.Class);

        Assert.Equal(["A.Tests.Foo", "A.Tests.Outer+Inner"], plan.Units.Select(u => u.Key));
        var foo = plan.Units[0];
        Assert.Equal(Project, foo.ProjectPath);
        Assert.Equal(@"FullyQualifiedName~A.Tests.Foo.", foo.Filter);
        Assert.Equal(["A.Tests.Foo.One", "A.Tests.Foo.Two(x: 1)", "A.Tests.Foo.Two(x: 2)"], foo.Tests);
        Assert.Equal(["Adds numbers"], plan.Units[1].Tests);
        Assert.Empty(plan.Obsolete);
    }

    [Fact]
    public void Create_TestGranularityPlansEachMethodByExactName()
    {
        var plan = ImpactBackfill.Create([Project], [Suite], _ => [], ImpactGranularity.Test);

        Assert.Equal(["A.Tests.Foo.One", "A.Tests.Foo.Two", "A.Tests.Outer+Inner.Adds"], plan.Units.Select(u => u.Key));
        Assert.Equal(@"FullyQualifiedName=A.Tests.Outer+Inner.Adds", plan.Units[2].Filter);
        Assert.Equal(2, plan.Units[1].Tests.Count);
    }

    [Fact]
    public void Create_SkipsCurrentUnits_PutsMissingBeforeOutdated_AndListsObsolete()
    {
        var plan = ImpactBackfill.Create([Project], [Suite], _ =>
        [
            Recorded("A.Tests.Foo", current: false, true, "A.Tests.Foo.One", "A.Tests.Foo.Two(x: 1)", "A.Tests.Foo.Two(x: 2)"),
            Recorded("A.Tests.Gone", current: true, true, "A.Tests.Gone.One"),
        ], ImpactGranularity.Class);

        Assert.Equal(["A.Tests.Outer+Inner", "A.Tests.Foo"], plan.Units.Select(u => u.Key));
        Assert.Equal(["A.Tests.Gone"], plan.Obsolete[Project]);

        var current = ImpactBackfill.Create([Project], [Suite], _ =>
        [
            Recorded("A.Tests.Foo", current: true, true, "A.Tests.Foo.One", "A.Tests.Foo.Two(x: 1)", "A.Tests.Foo.Two(x: 2)"),
            Recorded("A.Tests.Outer+Inner", current: true, true, "Adds numbers"),
        ], ImpactGranularity.Class);
        Assert.Empty(current.Units);
    }

    [Fact]
    public void Create_RemeasuresAUnitWhoseTestsChanged_AndHonoursSkip()
    {
        var plan = ImpactBackfill.Create([Project], [Suite], _ =>
        [
            Recorded("A.Tests.Foo", current: true, true, "A.Tests.Foo.One"),
        ], ImpactGranularity.Class, new HashSet<(string, string)> { (Project, "A.Tests.Outer+Inner") });

        Assert.Equal("A.Tests.Foo", Assert.Single(plan.Units).Key);
    }

    [Fact]
    public void Create_IgnoresProjectsWithoutResults()
    {
        var plan = ImpactBackfill.Create(["/src/B.Tests/B.Tests.csproj"], [Suite],
            _ => [Recorded("B.Tests.X", current: true, true, "B.Tests.X.One")], ImpactGranularity.Class);

        Assert.Empty(plan.Units);
        Assert.Empty(plan.Obsolete);
    }

    [Fact]
    public void Create_RejectsRunGranularity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ImpactBackfill.Create([Project], [Suite], _ => [], ImpactGranularity.Run));

    [Fact]
    public void Create_PlansLegacyUnitsAfterMissingAndOutdatedUnits()
    {
        var suite = new TestSuite("A.Tests",
        [
            Test("A.Tests.Foo.One"),
            Test("A.Tests.Outer.Inner"),
            Test("A.Tests.New.One"),
        ], DateTimeOffset.UtcNow, TimeSpan.Zero);
        var plan = ImpactBackfill.Create([Project], [suite], _ =>
        [
            Recorded("A.Tests.Foo", current: false, true, "A.Tests.Foo.One"),
            Recorded("A.Tests.Outer", current: true, false, "A.Tests.Outer.Inner"),
        ], ImpactGranularity.Class);

        Assert.Equal(["A.Tests.New", "A.Tests.Foo", "A.Tests.Outer"], plan.Units.Select(u => u.Key));
    }

    [Fact]
    public void Create_InterleavesProjectsWithinEachPriority()
    {
        const string otherProject = "/src/B.Tests/B.Tests.csproj";
        var a = new TestSuite("A.Tests",
        [
            Test("A.Tests.New1.Test"),
            Test("A.Tests.New2.Test"),
            Test("A.Tests.Old.Test"),
            Test("A.Tests.Legacy.Test"),
        ], DateTimeOffset.UtcNow, TimeSpan.Zero);
        var b = new TestSuite("B.Tests",
        [
            Test("B.Tests.New1.Test"),
            Test("B.Tests.Old.Test"),
            Test("B.Tests.Legacy.Test"),
        ], DateTimeOffset.UtcNow, TimeSpan.Zero);

        var plan = ImpactBackfill.Create([Project, otherProject], [a, b], project => project == Project
            ? [
                Recorded("A.Tests.Old", current: false, true, "A.Tests.Old.Test"),
                Recorded("A.Tests.Legacy", current: true, false, "A.Tests.Legacy.Test"),
            ]
            : [
                Recorded("B.Tests.Old", current: false, true, "B.Tests.Old.Test"),
                Recorded("B.Tests.Legacy", current: true, false, "B.Tests.Legacy.Test"),
            ], ImpactGranularity.Class);

        Assert.Equal(
            ["A.Tests.New1", "B.Tests.New1", "A.Tests.New2", "A.Tests.Old", "B.Tests.Old", "A.Tests.Legacy", "B.Tests.Legacy"],
            plan.Units.Select(unit => unit.Key));
    }
}
