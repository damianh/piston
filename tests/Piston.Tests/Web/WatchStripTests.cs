// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class WatchStripTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TestRunDto Run(string trigger, IReadOnlyList<string>? changedFiles = null, BuildResultDto? build = null,
        bool fullScope = false, double? elapsedMs = null) =>
        new("r1", "e1", 1, trigger, "Completed", null, fullScope ? "Full" : "Selective", false, fullScope, "Current", null,
            false, At, At, elapsedMs is { } ms ? At.AddMilliseconds(ms) : null, null, null, false, null,
            ["/repo/tests/A.Tests.csproj"], 3, 3, 0, 0, 0, Build: build, ChangedFiles: changedFiles);

    [Fact]
    public void FileChangeRun_NamesTheChangedFilesBuildAndScope()
    {
        var summary = WatchStrip.Summarize(
            Run("FileChange", ["/repo/src/Calc.cs", "/repo/src/Other.cs"],
                new BuildResultDto(BuildStatusDto.Succeeded, [], [], 4200), elapsedMs: 6000),
            "/repo");

        Assert.Equal("Calc.cs +1 saved", summary.Trigger);
        Assert.Equal($"src/Calc.cs{Environment.NewLine}src/Other.cs", summary.TriggerDetail);
        Assert.Equal("build 4.2s", summary.Build);
        Assert.False(summary.BuildFailed);
        Assert.Equal("1 project", summary.Scope);
        Assert.Equal("6.0s", summary.Duration);
    }

    [Fact]
    public void FileChangeRun_NamesASourceFileBeforeAnAtomicSaveTempFile()
    {
        var summary = WatchStrip.Summarize(Run("FileChange", ["/repo/src/sed3xYoqV", "/repo/src/Calc.cs"]), "/repo");

        Assert.Equal("Calc.cs +1 saved", summary.Trigger);
    }

    [Theory]
    [InlineData("Startup", "startup")]
    [InlineData("Forced", "run all")]
    [InlineData("Targeted", "run requested")]
    public void OtherTriggers_HaveShortLabels(string trigger, string label)
    {
        var summary = WatchStrip.Summarize(Run(trigger, fullScope: true), "/repo");

        Assert.Equal(label, summary.Trigger);
        Assert.Null(summary.TriggerDetail);
        Assert.Equal("every test", summary.Scope);
        Assert.Null(summary.Build);
        Assert.Null(summary.Duration);
    }

    [Fact]
    public void FailedAndSkippedBuildsAreDescribed()
    {
        var failed = WatchStrip.Summarize(Run("Forced", build: new BuildResultDto(BuildStatusDto.Failed, ["e"], [], 1500)), null);
        var skipped = WatchStrip.Summarize(Run("Forced", build: new BuildResultDto(BuildStatusDto.Skipped, [], [], 0)), null);

        Assert.Equal("build failed after 1.5s", failed.Build);
        Assert.True(failed.BuildFailed);
        Assert.Equal("build skipped", skipped.Build);
    }

    [Fact]
    public void ShortTestName_KeepsTheMethodAndArguments() =>
        Assert.Equal("Adds(a: \"x.y\")", WatchStrip.ShortTestName("App.Tests.CalcTests.Adds(a: \"x.y\")"));

    [Theory]
    [InlineData(1, 0, "passed")]
    [InlineData(3, 0, "all 3 passed")]
    [InlineData(0, 1, "0 passed, 1 skipped")]
    [InlineData(0, 3, "0 passed, 3 skipped")]
    [InlineData(2, 1, "2 passed, 1 skipped")]
    public void PassedSkippedLabel_DoesNotDescribeSkippedTestsAsPassing(int passed, int skipped, string expected)
    {
        var run = Run("Forced") with { TotalTests = passed + skipped, Passed = passed, Skipped = skipped };

        Assert.Equal(expected, WatchStrip.PassedSkippedLabel(run));
    }
}
