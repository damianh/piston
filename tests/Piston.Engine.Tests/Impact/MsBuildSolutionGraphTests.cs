using Piston.Engine.Impact;
using Xunit;

namespace Piston.Engine.Tests.Impact;

/// <summary>
/// Integration tests for <see cref="MsBuildSolutionGraph"/> using a synthetic 3-project solution.
/// Requires the .NET SDK to be installed.
/// </summary>
public sealed class MsBuildSolutionGraphTests : IAsyncLifetime
{
    // Solution layout:
    //   Lib/Lib.csproj               (source project)
    //   App/App.csproj               (references Lib)
    //   App.Tests/App.Tests.csproj   (test project, references App, IsTestProject=true)

    private string _root = string.Empty;
    private string _slnPath = string.Empty;
    private string _libCsproj = string.Empty;
    private string _appCsproj = string.Empty;
    private string _testsCsproj = string.Empty;
    private string _libCode = string.Empty;

    public async Task InitializeAsync()
    {
        // Register MSBuild BEFORE any MsBuildSolutionGraph is constructed.
        // This sets up the assembly resolver so Microsoft.Build.dll can be loaded.
        MsBuildLocatorGuard.EnsureRegistered();

        _root = Directory.CreateTempSubdirectory("piston-msbuild-test-").FullName;

        var libDir = Directory.CreateDirectory(Path.Combine(_root, "Lib")).FullName;
        var appDir = Directory.CreateDirectory(Path.Combine(_root, "App")).FullName;
        var testsDir = Directory.CreateDirectory(Path.Combine(_root, "App.Tests")).FullName;

        _libCsproj = Path.Combine(libDir, "Lib.csproj");
        _appCsproj = Path.Combine(appDir, "App.csproj");
        _testsCsproj = Path.Combine(testsDir, "App.Tests.csproj");
        _libCode = Path.Combine(libDir, "Code.cs");

        await File.WriteAllTextAsync(_libCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(Path.Combine(libDir, "Code.cs"), "namespace Lib; public class Code {}");

        await File.WriteAllTextAsync(_appCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(Path.Combine(appDir, "Program.cs"), "using Lib; var c = new Code();");

        await File.WriteAllTextAsync(_testsCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
                <PackageReference Include="xunit" Version="2.*" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
                <ProjectReference Include="..\App\App.csproj" />
              </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(Path.Combine(testsDir, "AppTests.cs"), """
            using Xunit;
            namespace App.Tests;
            public class AppTests { [Fact] public void Passes() => Assert.True(true); }
            """);

        // Create a .slnx referencing all three projects
        _slnPath = Path.Combine(_root, "Test.slnx");
        await File.WriteAllTextAsync(_slnPath, $"""
            <Solution>
              <Project Path="Lib/Lib.csproj" />
              <Project Path="App/App.csproj" />
              <Project Path="App.Tests/App.Tests.csproj" />
            </Solution>
            """);

        // Restore NuGet packages so MSBuild evaluation works
        await RunDotnetAsync("restore", _root);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    [Fact]
    public void AllProjectPaths_ContainsAllThreeProjects()
    {
        var sut = new MsBuildSolutionGraph(_slnPath);

        Assert.Equal(3, sut.AllProjectPaths.Count);
        Assert.Contains(sut.AllProjectPaths, p => p.EndsWith("Lib.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sut.AllProjectPaths, p => p.EndsWith("App.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sut.AllProjectPaths, p => p.EndsWith("App.Tests.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TestProjectPaths_ContainsOnlyAppTests()
    {
        var sut = new MsBuildSolutionGraph(_slnPath);

        Assert.Single(sut.TestProjectPaths);
        Assert.Contains(sut.TestProjectPaths, p => p.EndsWith("App.Tests.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IsTestProject_ReturnsTrueOnlyForTestProject()
    {
        var sut = new MsBuildSolutionGraph(_slnPath);

        Assert.True(sut.IsTestProject(_testsCsproj));
        Assert.False(sut.IsTestProject(_libCsproj));
        Assert.False(sut.IsTestProject(_appCsproj));
    }

    [Fact]
    public void FindOwningProject_ForCsFileInLib_ReturnsLibCsproj()
    {
        var sut = new MsBuildSolutionGraph(_slnPath);

        var owner = sut.FindOwningProject(_libCode);

        Assert.NotNull(owner);
        Assert.EndsWith("Lib.csproj", owner, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetTransitiveDependents_ForLib_ReturnsAppAndAppTests()
    {
        var sut = new MsBuildSolutionGraph(_slnPath);

        var dependents = sut.GetTransitiveDependents(_libCsproj);

        Assert.Contains(dependents, p => p.EndsWith("App.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dependents, p => p.EndsWith("App.Tests.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetTransitiveDependents_ForApp_ReturnsOnlyAppTests()
    {
        var sut = new MsBuildSolutionGraph(_slnPath);

        var dependents = sut.GetTransitiveDependents(_appCsproj);

        Assert.Contains(dependents, p => p.EndsWith("App.Tests.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dependents, p => p.EndsWith("Lib.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetTransitiveDependents_ForAppTests_ReturnsEmpty()
    {
        var sut = new MsBuildSolutionGraph(_slnPath);

        var dependents = sut.GetTransitiveDependents(_testsCsproj);

        Assert.Empty(dependents);
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
