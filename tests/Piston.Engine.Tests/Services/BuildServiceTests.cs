using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class BuildServiceOutputParsingTests
{
    // We test the parsing logic indirectly by running a real dotnet build
    // against a minimal in-memory project written to a temp directory.

    [Fact]
    public async Task BuildAsync_SuccessfulProject_ReturnsBuildStatusSucceeded()
    {
        var dir = Directory.CreateTempSubdirectory("piston-build-test-");
        try
        {
            // Create a minimal valid console project
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Test.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), "Console.WriteLine(\"hello\");");

            // Restore first
            var restoreResult = await RunDotnetAsync("restore", dir.FullName);
            Assert.Equal(0, restoreResult);

            var sut = new BuildService();
            var result = await sut.BuildAsync(
                Path.Combine(dir.FullName, "Test.csproj"),
                CancellationToken.None);

            Assert.Equal(BuildStatus.Succeeded, result.Status);
            Assert.Empty(result.Errors);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_ProjectWithCompileError_ReturnsBuildStatusFailed()
    {
        var dir = Directory.CreateTempSubdirectory("piston-build-test-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Test.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            // Deliberate compile error
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), "this is not valid csharp!!!;");

            var restoreResult = await RunDotnetAsync("restore", dir.FullName);
            Assert.Equal(0, restoreResult);

            var sut = new BuildService();
            var result = await sut.BuildAsync(
                Path.Combine(dir.FullName, "Test.csproj"),
                CancellationToken.None);

            Assert.Equal(BuildStatus.Failed, result.Status);
            Assert.NotEmpty(result.Errors);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_WhenCancelled_ReturnsBuildStatusFailed()
    {
        var dir = Directory.CreateTempSubdirectory("piston-build-test-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Test.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), "Console.WriteLine(\"hello\");");

            var restoreResult = await RunDotnetAsync("restore", dir.FullName);
            Assert.Equal(0, restoreResult);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var sut = new BuildService();
            var result = await sut.BuildAsync(
                Path.Combine(dir.FullName, "Test.csproj"),
                cts.Token);

            Assert.Equal(BuildStatus.Failed, result.Status);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_WithNullProjectPaths_BuildsSolution()
    {
        // null projectPaths → falls back to building the solution path (backward compat)
        var dir = Directory.CreateTempSubdirectory("piston-build-selective-");
        try
        {
            var csproj = Path.Combine(dir.FullName, "Test.csproj");
            await File.WriteAllTextAsync(csproj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), "Console.WriteLine(\"hi\");");

            await RunDotnetAsync("restore", dir.FullName);

            var sut = new BuildService();
            var result = await sut.BuildAsync(csproj, null, CancellationToken.None);

            Assert.Equal(BuildStatus.Succeeded, result.Status);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_WithExplicitProjectPath_Succeeds()
    {
        var dir = Directory.CreateTempSubdirectory("piston-build-selective-");
        try
        {
            var csproj = Path.Combine(dir.FullName, "Test.csproj");
            await File.WriteAllTextAsync(csproj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), "Console.WriteLine(\"hi\");");

            await RunDotnetAsync("restore", dir.FullName);

            var sut = new BuildService();
            var result = await sut.BuildAsync(
                solutionPath: csproj,
                projectPaths: [csproj],
                ct: CancellationToken.None);

            Assert.Equal(BuildStatus.Succeeded, result.Status);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_WithNonExistentProjectPath_FailsGracefully()
    {
        var sut = new BuildService();
        var result = await sut.BuildAsync(
            solutionPath: "solution.slnx",
            projectPaths: [@"C:\does\not\exist\Fake.csproj"],
            ct: CancellationToken.None);

        Assert.Equal(BuildStatus.Failed, result.Status);
    }

    [Fact]
    public async Task BuildAsync_WithTwoProjects_OneFailsOverallStatusFailed()
    {
        var goodDir = Directory.CreateTempSubdirectory("piston-build-good-");
        var badDir = Directory.CreateTempSubdirectory("piston-build-bad-");
        try
        {
            var goodCsproj = Path.Combine(goodDir.FullName, "Good.csproj");
            await File.WriteAllTextAsync(goodCsproj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(goodDir.FullName, "Program.cs"), "Console.WriteLine(\"good\");");
            await RunDotnetAsync("restore", goodDir.FullName);
            await RunDotnetAsync("build --no-restore", goodDir.FullName);

            var badCsproj = Path.Combine(badDir.FullName, "Bad.csproj");
            await File.WriteAllTextAsync(badCsproj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(badDir.FullName, "Program.cs"), "this is not valid;");
            await RunDotnetAsync("restore", badDir.FullName);

            var sut = new BuildService();
            var result = await sut.BuildAsync(
                solutionPath: "solution.slnx",
                projectPaths: [goodCsproj, badCsproj],
                ct: CancellationToken.None);

            Assert.Equal(BuildStatus.Failed, result.Status);
        }
        finally
        {
            goodDir.Delete(recursive: true);
            badDir.Delete(recursive: true);
        }
    }

    private static async Task<int> RunDotnetAsync(string args, string workDir)
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
        return p.ExitCode;
    }
}
