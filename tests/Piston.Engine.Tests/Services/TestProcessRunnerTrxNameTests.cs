// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TestProcessRunnerTrxNameTests
{
    [Theory]
    [InlineData("/tmp/r/piston-results_net8.0_20261011101112.trx", "net8.0")]
    [InlineData("/tmp/r/piston-results_net10.0-windows_20261011101112.trx", "net10.0-windows")]
    [InlineData("/tmp/r/piston-results_netstandard2.0_20261011101112.trx", "netstandard2.0")]
    [InlineData("/tmp/r/piston-results.trx", null)]
    [InlineData("/tmp/r/other_net8.0_20261011101112.trx", null)]
    public void TargetFrameworkOfTrx_ReadsTheFrameworkFromTheLoggerFileName(string path, string? expected) =>
        Assert.Equal(expected, TestProcessRunner.TargetFrameworkOfTrx(path));

    [Fact]
    public void WorkingDirectoryFor_IsTheProjectDirectory_SoItsGlobalJsonApplies()
    {
        var dir = Directory.CreateTempSubdirectory("piston-wd-").FullName;
        try
        {
            Assert.Equal(dir, TestProcessRunner.WorkingDirectoryFor(Path.Combine(dir, "A.Tests.csproj")));
            Assert.Null(TestProcessRunner.WorkingDirectoryFor(Path.Combine(dir, "missing", "A.Tests.csproj")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WorkingDirectoryFor_LongProjectDirectoryIsStartableAndPreservesSdkSelection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var temporary = Directory.CreateTempSubdirectory("piston-vstest-long-").FullName;
        var sdkDirectory = temporary;
        try
        {
            var sdkVersionResult = await TestProcess.RunDotnetResultAsync("--version", Environment.CurrentDirectory);
            Assert.True(sdkVersionResult.ExitCode == 0, sdkVersionResult.Output);
            var sdkVersion = sdkVersionResult.Output
                .Split(Environment.NewLine)
                .Single(line => line.StartsWith("[stdout] ", StringComparison.Ordinal))["[stdout] ".Length..];

            while (sdkDirectory.Length < 220)
            {
                sdkDirectory = Path.Combine(sdkDirectory, "sdk-scope");
                _ = Directory.CreateDirectory(sdkDirectory);
            }

            await File.WriteAllTextAsync(Path.Combine(sdkDirectory, "global.json"),
                $$"""
                {
                  "sdk": {
                    "version": "{{sdkVersion}}",
                    "rollForward": "disable"
                  },
                  "test": {
                    "runner": "VSTest"
                  }
                }
                """, TestContext.Current.CancellationToken);

            var projectDirectory = sdkDirectory;
            while (projectDirectory.Length <= 280)
            {
                projectDirectory = Path.Combine(projectDirectory, "nested-directory");
                _ = Directory.CreateDirectory(projectDirectory);
            }

            Assert.True(projectDirectory.Length > 260);
            var projectPath = Path.Combine(projectDirectory, "LongPathTests.csproj");
            var workingDirectory = TestProcessRunner.WorkingDirectoryFor(projectPath);
            Assert.NotNull(workingDirectory);
            Assert.True(workingDirectory.Length < 259);
            Assert.False(Path.GetRelativePath(sdkDirectory, workingDirectory).StartsWith("..", StringComparison.Ordinal));

            var selectedSdk = await TestProcess.RunDotnetResultAsync("--version", workingDirectory);
            Assert.True(selectedSdk.ExitCode == 0, selectedSdk.Output);
            Assert.Contains($"[stdout] {sdkVersion}", selectedSdk.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public async Task CoverageRequestedWithoutAReportRemovesTheVSTestResultsDirectory()
    {
        var root = Directory.CreateTempSubdirectory("piston-vstest-no-report-").FullName;
        try
        {
            var result = await TestProcessRunner.RunAsync(
                Path.Combine(root, "Missing.csproj"), filter: null, collectCoverage: true,
                onProgress: null, parser: new TrxResultParser(), ct: TestContext.Current.CancellationToken);

            var args = Assert.Single(result.Log!.Entries,
                entry => entry.Message.StartsWith("Args: dotnet ", StringComparison.Ordinal)).Message;
            const string marker = "--results-directory \"";
            var start = args.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0);
            var directory = args[(start + marker.Length)..].Split('"')[0];
            Assert.StartsWith(Path.GetTempPath(), directory, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--collect \"XPlat Code Coverage\"", args, StringComparison.Ordinal);
            Assert.Empty(result.CoverageReportPaths);
            Assert.Equal("The test run produced no Cobertura coverage report.", result.CoverageIssue);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
