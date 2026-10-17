// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class BuildServiceOutputParsingTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    // We test the parsing logic indirectly by running a real dotnet build
    // against a minimal in-memory project written to a temp directory.

    [Fact]
    public async Task BuildAsync_ParallelMsBuildNodes_DisablesNodeReuseAndCompletes()
    {
        var root = Directory.CreateTempSubdirectory("piston-build-node-reuse-test-").FullName;
        try
        {
            foreach (var name in new[] { "First", "Second" })
            {
                await File.WriteAllTextAsync(Path.Combine(root, $"{name}.proj"), """
                    <Project>
                      <Target Name="Build">
                        <Error Condition="'$(MSBUILDDISABLENODEREUSE)' != '1'" Text="Node reuse guard is missing." />
                      </Target>
                    </Project>
                    """, _ct);
            }
            var project = Path.Combine(root, "All.proj");
            await File.WriteAllTextAsync(project, """
                <Project>
                  <Target Name="Restore" />
                  <Target Name="Build">
                    <MSBuild Projects="First.proj;Second.proj" BuildInParallel="true" />
                  </Target>
                </Project>
                """, _ct);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var result = await new BuildService().BuildAsync(project, cts.Token);

            Assert.Equal(BuildStatus.Succeeded, result.Status);
            Assert.Empty(result.Errors);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_RelativeSolutionPathWithDirectory_BuildsTheSolution()
    {
        // Relative to the test process, and including directories, unlike the build's own working directory.
        var relativeRoot = $"piston-build-relative-path-test-{Guid.NewGuid():N}";
        var root = Path.GetFullPath(relativeRoot);
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;
            var project = Path.Combine(sub, "All.proj");
            await File.WriteAllTextAsync(project, """
                <Project>
                  <Target Name="Restore" />
                  <Target Name="Build" />
                </Project>
                """, _ct);
            var relativePath = Path.Combine(relativeRoot, "sub", "All.proj");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var result = await new BuildService().BuildAsync(relativePath, cts.Token);

            Assert.Equal(BuildStatus.Succeeded, result.Status);
            Assert.Empty(result.Errors);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
                """, _ct);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), "Console.WriteLine(\"hello\");", _ct);

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
                """, _ct);
            // Deliberate compile error
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), "this is not valid csharp!!!;", _ct);

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
                """, _ct);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), "Console.WriteLine(\"hello\");", _ct);

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
                """, _ct);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), "Console.WriteLine(\"hi\");", _ct);

            _ = await RunDotnetAsync("restore", dir.FullName);

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
                """, _ct);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), "Console.WriteLine(\"hi\");", _ct);

            _ = await RunDotnetAsync("restore", dir.FullName);

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
                """, _ct);
            await File.WriteAllTextAsync(Path.Combine(goodDir.FullName, "Program.cs"), "Console.WriteLine(\"good\");", _ct);
            _ = await RunDotnetAsync("restore", goodDir.FullName);
            _ = await RunDotnetAsync("build --no-restore", goodDir.FullName);

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
                """, _ct);
            await File.WriteAllTextAsync(Path.Combine(badDir.FullName, "Program.cs"), "this is not valid;", _ct);
            _ = await RunDotnetAsync("restore", badDir.FullName);

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

    [Fact]
    public async Task BuildAsync_CodelessMsBuildError_IsReported()
    {
        var root = Directory.CreateTempSubdirectory("piston-build-codeless-error-test-").FullName;
        try
        {
            var project = Path.Combine(root, "Fail.proj");
            await File.WriteAllTextAsync(project, """
                <Project DefaultTargets="Build">
                  <Target Name="Restore" />
                  <Target Name="Build">
                    <Error Text="Codeless failure from target." />
                  </Target>
                </Project>
                """, _ct);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var result = await new BuildService().BuildAsync(project, cts.Token);

            Assert.Equal(BuildStatus.Failed, result.Status);
            Assert.NotEmpty(result.Errors);
            Assert.All(result.Errors, e => Assert.Contains("Codeless failure from target.", e));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<int> RunDotnetAsync(string args, string workDir) =>
        TestProcess.RunDotnetAsync(args, workDir);
}
