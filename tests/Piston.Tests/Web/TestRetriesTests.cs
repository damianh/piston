// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class TestRetriesTests
{
    private static TestResultDto Result(TestStatusDto status, params TestAttemptDto[] attempts) =>
        new("A.B", "B", status, 1, null, null, null, null, Attempts: attempts.Length == 0 ? null : attempts);

    private static TestAttemptDto Attempt(TestStatusDto status, string? message = null) => new(status, 1, message, null);

    [Fact]
    public void DescribesHowARetriedTestEnded()
    {
        Assert.Null(TestRetries.Describe(Result(TestStatusDto.Passed)));
        Assert.Equal("Passed on attempt 2 after 1 failure", TestRetries.Describe(Result(TestStatusDto.Passed, Attempt(TestStatusDto.Failed))));
        Assert.Equal("Passed on attempt 3 after 2 failures",
            TestRetries.Describe(Result(TestStatusDto.Passed, Attempt(TestStatusDto.Failed), Attempt(TestStatusDto.Failed))));
        Assert.Equal("Failed all 2 attempts", TestRetries.Describe(Result(TestStatusDto.Failed, Attempt(TestStatusDto.Failed))));
    }

    [Fact]
    public void DescribesStoredRetries()
    {
        Assert.Null(TestRetries.Describe(new StoredTestResultDto("P", "A.B", "Passed", 1, null, null)));
        Assert.Equal("Passed on attempt 2 after 1 failure", TestRetries.Describe(
            new StoredTestResultDto("P", "A.B", "Passed", 1, null, null, [new StoredAttemptDto("Failed", 1, "boom")])));
    }

    [Fact]
    public void FirstLineFallsBackToStatus()
    {
        Assert.Equal("boom", TestRetries.FirstLine("boom\r\nat X()", "Failed"));
        Assert.Equal("Failed", TestRetries.FirstLine(null, "Failed"));
        Assert.Equal("Failed", TestRetries.FirstLine("  ", "Failed"));
    }

    [Fact]
    public void FlakyTitleCarriesTheEvidence()
    {
        var at = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var evidence = new FlakyEvidenceDto("abc", 1, 1, 1, ["feature-x"], "Passed 1 and failed 1 time(s) on source abc.",
        [
            new("r2", at, "Passed", 1, null),
            new("r9", at, "Failed", 0, "feature-x"),
        ]);

        Assert.Equal("Flaky: Passed 1 and failed 1 time(s) on source abc.", TestRetries.FlakyTitle(evidence));
        Assert.Equal("Flaky: passed and failed on the same source", TestRetries.FlakyTitle(null));
        var lines = TestRetries.DescribeOutcomes(evidence).Split(Environment.NewLine);
        Assert.EndsWith("Passed run r2 retry 1", lines[0]);
        Assert.EndsWith("Failed run r9 in worktree feature-x", lines[1]);
    }
}
