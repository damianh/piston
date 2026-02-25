using Piston.Core.Models;
using Piston.Core.Services;
using Xunit;

namespace Piston.Core.Tests.Services;

/// <summary>
/// Integration tests that run dotnet test against a real minimal xUnit project
/// and verify TestRunnerService picks up the TRX results correctly.
/// </summary>
public sealed class TestRunnerServiceTests : IAsyncLifetime
{
    private string _projectDir = string.Empty;
    private string _projectFile = string.Empty;

    public async Task InitializeAsync()
    {
        _projectDir = Directory.CreateTempSubdirectory("piston-runner-test-").FullName;
        _projectFile = Path.Combine(_projectDir, "RunnerTest.csproj");

        await File.WriteAllTextAsync(_projectFile, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
                <PackageReference Include="xunit" Version="2.*" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
              </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(Path.Combine(_projectDir, "Tests.cs"), """
            using Xunit;
            namespace RunnerTest;
            public class Tests
            {
                [Fact] public void Passes() => Assert.True(true);
                [Fact] public void Fails() => Assert.Fail("intentional failure");
            }
            """);

        // Restore + build first so RunTestsAsync can use --no-build
        await RunDotnetAsync("restore", _projectDir);
        await RunDotnetAsync("build --no-restore", _projectDir);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_projectDir, recursive: true); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunTestsAsync_ReturnsResults_WithPassedAndFailed()
    {
        var sut = new TestRunnerService(new TrxResultParser());

        var suites = await sut.RunTestsAsync(_projectFile, CancellationToken.None);

        Assert.NotEmpty(suites);
        var allTests = suites.SelectMany(s => s.Tests).ToList();
        Assert.Contains(allTests, t => t.Status == TestStatus.Passed);
        Assert.Contains(allTests, t => t.Status == TestStatus.Failed);
    }

    [Fact]
    public async Task RunTestsAsync_WhenCancelled_ReturnsEmpty()
    {
        var sut = new TestRunnerService(new TrxResultParser());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var suites = await sut.RunTestsAsync(_projectFile, cts.Token);

        Assert.Empty(suites);
    }

    [Fact]
    public async Task RunTestsAsync_FullyQualifiedNames_IncludeClassName()
    {
        var sut = new TestRunnerService(new TrxResultParser());

        var suites = await sut.RunTestsAsync(_projectFile, CancellationToken.None);

        var allTests = suites.SelectMany(s => s.Tests).ToList();
        Assert.All(allTests, t =>
            Assert.Contains("RunnerTest.Tests.", t.FullyQualifiedName));
    }

    private static async Task RunDotnetAsync(string args, string workDir)
    {
        using var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("dotnet", args)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
    }
}
