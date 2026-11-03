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
