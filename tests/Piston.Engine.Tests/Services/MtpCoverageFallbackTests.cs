// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class MtpCoverageFallbackTests(MtpCoverageFallbackFixture fixture) : IClassFixture<MtpCoverageFallbackFixture>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_UnavailableMultiTargetCoverageStillRunsTheTests(bool missingExtension)
    {
        var coverage = missingExtension ? MtpCoverage.MultiTargetExtensionMissing : MtpCoverage.OutputUnverified;
        var result = await RunOneBuiltTestAsync(coverage);

        AssertPassingFixtureTest(result);
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

        AssertPassingFixtureTest(result);
        Assert.Empty(result.CoverageReportPaths);
        Assert.Equal("The test app does not include Microsoft.Testing.Extensions.CodeCoverage.", result.CoverageIssue);
        Assert.Contains(result.Log!.Entries, entry => entry.Message == result.CoverageIssue);
    }

    [Fact]
    public async Task ExecuteAsync_MultiTargetUnverifiedStillRunsTheTests()
    {
        var strategy = new MtpTestExecutionStrategy(
            _ => fixture.AppPath, fixture.Root, isMultiTargeted: _ => true,
            getMtpTargetOutputs: _ => [("net10.0", fixture.AppPath)]);

        var result = await strategy.ExecuteAsync(new ProjectTestRequest(fixture.Project, fixture.Filter, true),
            onProgress: null, TestContext.Current.CancellationToken);

        AssertPassingFixtureTest(result);
        Assert.Empty(result.CoverageReportPaths);
        Assert.Equal("Coverage was skipped because collector availability could not be verified for every target framework.",
            result.CoverageIssue);
        var args = Assert.Single(result.Log!.Entries,
            entry => entry.Message.StartsWith("Args: dotnet ", StringComparison.Ordinal)).Message;
        Assert.DoesNotContain("--coverage", args, StringComparison.Ordinal);
    }

    private Task<ProjectTestResult> RunOneBuiltTestAsync(MtpCoverage coverage) =>
        MtpTestProcessRunner.RunAsync(fixture.Project, appPath: null, fixture.Root, fixture.Filter, onProgress: null,
            TestContext.Current.CancellationToken, coverage);

    private static void AssertPassingFixtureTest(ProjectTestResult result)
    {
        Assert.True(result.RunnerError is null,
            $"{result.RunnerError}{Environment.NewLine}{string.Join(Environment.NewLine, result.Log?.Entries.Select(entry => entry.Message) ?? [])}");
        var test = Assert.Single(result.Suites.SelectMany(suite => suite.Tests));
        Assert.Equal("MtpFallback.SampleTests.PassingTest", test.FullyQualifiedName);
        Assert.True(test.Status == TestStatus.Passed, test.ErrorMessage);
    }
}

public sealed class MtpCoverageFallbackFixture : IAsyncLifetime
{
    public string Root { get; private set; } = string.Empty;
    public string Project => Path.Combine(Root, "MtpFallback.csproj");
    public string AppPath => Path.Combine(Root, "bin", "Debug", "net10.0", "MtpFallback.dll");
    public string Filter => "FullyQualifiedName=MtpFallback.SampleTests.PassingTest";

    public async ValueTask InitializeAsync()
    {
        Root = Directory.CreateTempSubdirectory("piston-mtp-fallback-").FullName;
        await File.WriteAllTextAsync(Path.Combine(Root, "global.json"), """
            {
              "test": {
                "runner": "Microsoft.Testing.Platform"
              }
            }
            """);
        await File.WriteAllTextAsync(Project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <IsTestingPlatformApplication>true</IsTestingPlatformApplication>
                <NoWarn>$(NoWarn);NU1507</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3.core.mtp-v2" Version="4.0.2" />
                <PackageReference Include="xunit.v3.assert" Version="4.0.2" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(Root, "Tests.cs"), """
            namespace MtpFallback;

            public sealed class SampleTests
            {
                [Xunit.Fact]
                public void PassingTest() => Xunit.Assert.True(
                    System.IO.File.Exists(System.IO.Path.Combine(System.AppContext.BaseDirectory, "MtpFallback.dll")));
            }
            """);

        _ = await TestProcess.RunDotnetAsync("restore", Root);
        _ = await TestProcess.RunDotnetAsync("build --configuration Debug --no-restore", Root);
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(Root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
