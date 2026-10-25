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
        Assert.InRange(error.Length, 1, RetentionLimits.MaxBuildDiagnosticLength);
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
    public void Diagnostics_AreBoundedInCountAndLength()
    {
        var collector = new BuildOutputCollector();
        collector.AddOutputLine($"/w/A.csproj : error : {new string('x', RetentionLimits.MaxBuildDiagnosticLength * 2)}");
        for (var i = 0; i <= RetentionLimits.MaxBuildDiagnostics; i++)
        {
            collector.AddOutputLine($"/w/A.csproj : error : error {i}");
            collector.AddOutputLine($"/w/A.csproj : warning : warning {i}");
        }

        var result = collector.ToResult(1, TimeSpan.Zero);

        Assert.Equal(RetentionLimits.MaxBuildDiagnostics, result.Errors.Count);
        Assert.Equal(2, result.OmittedErrors);
        Assert.Equal(RetentionLimits.MaxBuildDiagnostics, result.Warnings.Count);
        Assert.Equal(1, result.OmittedWarnings);
        Assert.All(result.Errors, e => Assert.InRange(e.Length, 1, RetentionLimits.MaxBuildDiagnosticLength));
        Assert.Contains("characters truncated", result.Errors[0], StringComparison.Ordinal);
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

    [Theory]
    [InlineData("  Piston.Engine -> /w/src/Piston.Engine/bin/Debug/net10.0/Piston.Engine.dll", "Piston.Engine")]
    [InlineData("My-Lib.Core -> C:\\w\\bin\\My-Lib.Core.dll", "My-Lib.Core")]
    [InlineData("  My App.Tests -> /w/bin/My App.Tests.dll", "My App.Tests")]
    public void BuiltProjectLines_YieldProjectName(string line, string expected)
    {
        Assert.True(BuildOutputCollector.TryParseBuiltProject(line, out var name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("  Piston.Web (Blazor output) -> /w/src/Piston.Web/bin/Debug/net10.0/wwwroot")]
    [InlineData("  My App (Blazor output) -> /w/src/My App/bin/Debug/net10.0/wwwroot")]
    [InlineData("  /w/A.cs(3,5): error CS0103: x -> y [/w/A.csproj]")]
    [InlineData("  CSC : warning CS1234: a -> b")]
    [InlineData("  MSBUILD : error MSB1009: a -> b")]
    [InlineData("  Determining projects to restore...")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherLines_AreNotBuiltProjects(string? line) =>
        Assert.False(BuildOutputCollector.TryParseBuiltProject(line, out _));
}
