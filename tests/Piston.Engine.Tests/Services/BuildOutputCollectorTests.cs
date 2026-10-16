// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class BuildOutputCollectorTests
{
    [Fact]
    public void CodedError_IsRecognized()
    {
        var collector = new BuildOutputCollector();
        collector.AddOutputLine("  /w/A.cs(3,5): error CS0103: The name 'x' does not exist [/w/A.csproj]");
        collector.AddOutputLine("Build FAILED.");

        var result = collector.ToResult(1, TimeSpan.FromSeconds(1));

        Assert.Equal(BuildStatus.Failed, result.Status);
        Assert.Equal(["/w/A.cs(3,5): error CS0103: The name 'x' does not exist [/w/A.csproj]"], result.Errors);
    }

    [Fact]
    public void CodelessError_IsRecognized()
    {
        var collector = new BuildOutputCollector();
        collector.AddOutputLine("/w/A.csproj : error : Something went wrong.");

        var result = collector.ToResult(1, TimeSpan.Zero);

        Assert.Equal(BuildStatus.Failed, result.Status);
        Assert.Equal(["/w/A.csproj : error : Something went wrong."], result.Errors);
    }

    [Fact]
    public void CodedAndCodelessWarnings_AreRecognized()
    {
        var collector = new BuildOutputCollector();
        collector.AddOutputLine("/w/A.cs(1,1): warning CS0168: unused");
        collector.AddOutputLine("/w/A.csproj : warning : codeless");

        var result = collector.ToResult(0, TimeSpan.Zero);

        Assert.Equal(BuildStatus.Succeeded, result.Status);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void NonZeroExitWithoutErrorLines_SynthesizesErrorWithExitCodeAndTail()
    {
        var collector = new BuildOutputCollector(maxTailLines: 3);
        foreach (var line in new[] { "line 1", "line 2", "line 3", "The process cannot access the file because it is being used.", "Build FAILED." })
        {
            collector.AddOutputLine(line);
        }

        var result = collector.ToResult(137, TimeSpan.Zero);

        Assert.Equal(BuildStatus.Failed, result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Contains("exited with code 137", error);
        Assert.Contains("Last 3 line(s) of output:", error);
        Assert.EndsWith("line 3\nThe process cannot access the file because it is being used.\nBuild FAILED.", error);
        Assert.DoesNotContain("line 2", error);
    }

    [Fact]
    public void NonZeroExitWithoutOutput_SynthesizesErrorWithExitCode()
    {
        var result = new BuildOutputCollector().ToResult(1, TimeSpan.Zero);

        var error = Assert.Single(result.Errors);
        Assert.Contains("exited with code 1", error);
        Assert.Contains("No output was captured.", error);
    }

    [Fact]
    public void TailLines_AreBoundedInLength()
    {
        var collector = new BuildOutputCollector();
        collector.AddOutputLine(new string('x', BuildOutputCollector.MaxTailLineLength * 3));

        var error = Assert.Single(collector.ToResult(1, TimeSpan.Zero).Errors);

        Assert.DoesNotContain(new string('x', BuildOutputCollector.MaxTailLineLength + 1), error);
        Assert.EndsWith("…", error);
    }

    [Fact]
    public void ExitZeroWithoutErrors_Succeeds()
    {
        var collector = new BuildOutputCollector();
        collector.AddOutputLine("Build succeeded.");
        collector.AddOutputLine("    0 Error(s)");

        var result = collector.ToResult(0, TimeSpan.FromMilliseconds(5));

        Assert.Equal(BuildStatus.Succeeded, result.Status);
        Assert.Empty(result.Errors);
        Assert.Equal(TimeSpan.FromMilliseconds(5), result.Duration);
    }

    [Fact]
    public void StandardErrorLines_AreErrors()
    {
        var collector = new BuildOutputCollector();
        collector.AddErrorLine("Unhandled exception: boom");
        collector.AddErrorLine("   ");

        var result = collector.ToResult(1, TimeSpan.Zero);

        Assert.Equal(["Unhandled exception: boom"], result.Errors);
    }

    [Fact]
    public void CancelledResult_IsFailedWithoutSynthesizedError()
    {
        var collector = new BuildOutputCollector();
        collector.AddOutputLine("Restoring...");

        var result = collector.ToCancelledResult(TimeSpan.Zero);

        Assert.Equal(BuildStatus.Failed, result.Status);
        Assert.Empty(result.Errors);
    }
}
