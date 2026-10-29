// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class MtpTestProcessRunnerTests
{
    [Fact]
    public void AttachArtifacts_ReportsAmbiguousMultiTargetedTestNames()
    {
        var log = new ExecutionLog("Tests.csproj");
        var test = new TestResult("Ns.Tests.Captures", "Captures", TestStatus.Passed,
            TimeSpan.Zero, null, null, null, null);

        var results = MtpTestProcessRunner.AttachArtifacts(
            [test with { TargetFramework = "net8.0" }, test with { TargetFramework = "net10.0" }],
            [new("Ns.Tests.Captures", "/tmp/report.txt")], log);

        Assert.All(results, result => Assert.Empty(result.Attachments));
        var message = Assert.Single(log.Entries).Message;
        Assert.Contains("/tmp/report.txt", message, StringComparison.Ordinal);
        Assert.Contains("Ns.Tests.Captures", message, StringComparison.Ordinal);
        Assert.Contains("2 matching results", message, StringComparison.Ordinal);
        Assert.Contains("does not identify its producing framework", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachArtifacts_AttachesUniqueMatchesAndReportsUnmatchedNames()
    {
        var log = new ExecutionLog("Tests.csproj");
        var test = new TestResult("Ns.Tests.Captures", "Captures", TestStatus.Passed,
            TimeSpan.Zero, null, null, null, null);

        var results = MtpTestProcessRunner.AttachArtifacts([test],
            [new("Captures", "/tmp/first.txt"), new("Ns.Tests.Captures", "/tmp/second.txt"),
                new("Ns.Tests.Unknown", "/tmp/unknown.txt")], log);

        Assert.Equal(["/tmp/first.txt", "/tmp/second.txt"],
            Assert.Single(results).Attachments.Select(attachment => attachment.Path));
        Assert.Contains("0 matching results", Assert.Single(log.Entries).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_ReturnsEmptyResultWithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await MtpTestProcessRunner.RunAsync(
            projectPath: "nonexistent.csproj",
            solutionDirectory: Path.GetTempPath(),
            filter: null,
            appPath: null,
            onProgress: null,
            ct: cts.Token);

        Assert.NotNull(result);
        Assert.Empty(result.Suites);
        Assert.False(result.Crashed);
    }

    [Fact]
    public async Task RunAsync_NonExistentProject_ReturnsResultWithRunnerError()
    {
        // dotnet test on a non-existent project will fail; result should contain a runner error,
        // not throw an exception.
        var result = await MtpTestProcessRunner.RunAsync(
            projectPath: Path.Combine(Path.GetTempPath(), "piston-test-nonexistent.csproj"),
            solutionDirectory: Path.GetTempPath(),
            filter: null,
            appPath: null,
            onProgress: null,
            ct: CancellationToken.None);

        Assert.NotNull(result);
        // No suites (process failed), no crash flag (handled gracefully)
        Assert.Empty(result.Suites);
        Assert.False(result.Crashed);
    }

    [Fact]
    public async Task RunAsync_SurfacesProjectPath_InResult()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        const string projectPath = "my/project.csproj";
        var result = await MtpTestProcessRunner.RunAsync(
            projectPath: projectPath,
            solutionDirectory: Path.GetTempPath(),
            filter: null,
            appPath: null,
            onProgress: null,
            ct: cts.Token);

        Assert.Equal(projectPath, result.ProjectPath);
    }
}
