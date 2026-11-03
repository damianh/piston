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

    [Fact]
    public async Task RunAsync_NoCoverageReportRemovesTheResultsDirectory()
    {
        var root = Directory.CreateTempSubdirectory("piston-mtp-no-report-").FullName;
        try
        {
            var result = await MtpTestProcessRunner.RunAsync(
                projectPath: Path.Combine(root, "Missing.csproj"),
                appPath: null,
                solutionDirectory: root,
                filter: null,
                onProgress: null,
                ct: TestContext.Current.CancellationToken,
                coverage: MtpCoverage.Collect);

            var args = Assert.Single(result.Log!.Entries,
                entry => entry.Message.StartsWith("Args: dotnet ", StringComparison.Ordinal)).Message;
            const string marker = "--results-directory \"";
            var start = args.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0);
            var directory = args[(start + marker.Length)..].Split('"')[0];
            Assert.StartsWith(Path.GetTempPath(), directory, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(result.CoverageReportPaths);
            Assert.Equal("The test run produced no Cobertura coverage report.", result.CoverageIssue);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_UnavailableMultiTargetCoverageStillRunsTheTests(bool missingExtension)
    {
        var coverage = missingExtension ? MtpCoverage.MultiTargetExtensionMissing : MtpCoverage.OutputUnverified;
        var result = await RunOneBuiltTestAsync(coverage);

        Assert.Null(result.RunnerError);
        Assert.Equal(TestStatus.Passed, Assert.Single(result.Suites.SelectMany(suite => suite.Tests)).Status);
        Assert.Empty(result.CoverageReportPaths);
        Assert.Equal(missingExtension
            ? "At least one target framework does not include Microsoft.Testing.Extensions.CodeCoverage; coverage was skipped so all targets can run."
            : "Coverage was skipped because collector availability could not be verified for every target framework.",
            result.CoverageIssue);
        var args = Assert.Single(result.Log!.Entries,
            entry => entry.Message.StartsWith("Args: dotnet ", StringComparison.Ordinal)).Message;
        Assert.DoesNotContain("--coverage", args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MissingSingleTargetExtensionRunsTheTestsAndReportsWhy()
    {
        var result = await RunOneBuiltTestAsync(MtpCoverage.ExtensionMissing);

        Assert.Null(result.RunnerError);
        Assert.Equal(TestStatus.Passed, Assert.Single(result.Suites.SelectMany(suite => suite.Tests)).Status);
        Assert.Empty(result.CoverageReportPaths);
        Assert.Equal("The test app does not include Microsoft.Testing.Extensions.CodeCoverage.", result.CoverageIssue);
        Assert.Contains(result.Log!.Entries, entry => entry.Message == result.CoverageIssue);
    }

    private static Task<ProjectTestResult> RunOneBuiltTestAsync(MtpCoverage coverage)
    {
        var (root, project, filter) = BuiltTestFixture();
        return MtpTestProcessRunner.RunAsync(project, appPath: null, root, filter, onProgress: null,
            TestContext.Current.CancellationToken, coverage);
    }

    internal static (string Root, string Project, string Filter) BuiltTestFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Piston.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find Piston.slnx above the test output directory.");
        var project = Path.Combine(root, "tests", "Piston.Engine.Tests", "Piston.Engine.Tests.csproj");
        const string filter =
            "FullyQualifiedName=Piston.Engine.Tests.Services.MtpTestExecutionStrategyTests.HasCoverageExtension_LooksForTheExtensionNextToTheTestApp";
        return (root, project, filter);
    }
}
