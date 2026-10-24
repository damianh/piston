// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class RunComparisonTests
{
    private static StoredRunDto Run(string id, params StoredTestResultDto[] results) =>
        new(new StoredRunSummaryDto(id, "Forced", "Completed", "Full", true, "Current", DateTimeOffset.UnixEpoch, null, null, 0, 0, 0), results);

    private static StoredTestResultDto Result(string test, string status, double ms = 10, string project = "A.Tests") =>
        new(project, test, status, ms, status == "Failed" ? "boom" : null, null);

    [Fact]
    public void ClassifiesEachTestByItsOutcomeInBothRuns()
    {
        var before = Run("r1",
            Result("A.Breaks", "Passed"),
            Result("A.Heals", "Failed"),
            Result("A.StillBroken", "Failed"),
            Result("A.Gone", "Passed"),
            Result("A.WasSkipped", "Skipped"));
        var after = Run("r2",
            Result("A.Breaks", "Failed"),
            Result("A.Heals", "Passed"),
            Result("A.StillBroken", "Failed"),
            Result("A.New", "Failed"),
            Result("A.WasSkipped", "Failed"));

        var comparison = RunComparison.Compare(before, after);

        Assert.Equal(["A.Breaks", "A.New", "A.WasSkipped"], comparison.NewFailures.Select(e => e.Test));
        Assert.Equal(["A.Heals"], comparison.Fixed.Select(e => e.Test));
        Assert.Equal(["A.StillBroken"], comparison.StillFailing.Select(e => e.Test));
        Assert.Equal(["A.New"], comparison.Added.Select(e => e.Test));
        Assert.Equal(["A.Gone"], comparison.Missing.Select(e => e.Test));
        Assert.Null(comparison.Added[0].Before);
        Assert.False(comparison.IsEmpty);
    }

    [Fact]
    public void NewlySlowNeedsTwiceTheTimeAndAtLeast100MsMoreOrderedBySlowdown()
    {
        var before = Run("r1",
            Result("A.Doubled", "Passed", 200),
            Result("A.Tripled", "Passed", 400),
            Result("A.Jitter", "Passed", 5),
            Result("A.Slower", "Passed", 1000),
            Result("A.NowFails", "Passed", 100));
        var after = Run("r2",
            Result("A.Doubled", "Passed", 400),
            Result("A.Tripled", "Passed", 1200),
            Result("A.Jitter", "Passed", 50),
            Result("A.Slower", "Passed", 1900),
            Result("A.NowFails", "Failed", 900));

        var slow = RunComparison.Compare(before, after).NewlySlow;

        Assert.Equal(["A.Tripled", "A.Doubled"], slow.Select(e => e.Test));
        Assert.Equal(800, slow[0].DeltaMs);
    }

    [Fact]
    public void TestsAreMatchedByProjectAndName()
    {
        var before = Run("r1", Result("Shared.Test", "Passed", project: "A.Tests"));
        var after = Run("r2", Result("Shared.Test", "Failed", project: "B.Tests"), Result("Shared.Test", "Passed", project: "A.Tests"));

        var comparison = RunComparison.Compare(before, after);

        Assert.Equal("B.Tests", Assert.Single(comparison.NewFailures).Project);
        Assert.Empty(comparison.Missing);
    }

    [Fact]
    public void IdenticalRunsHaveNoDifferences() =>
        Assert.True(RunComparison.Compare(Run("r1", Result("A.One", "Passed")), Run("r2", Result("A.One", "Passed"))).IsEmpty);
}
