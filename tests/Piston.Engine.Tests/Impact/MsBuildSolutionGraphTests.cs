// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
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

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private string _root = string.Empty;
    private string _slnPath = string.Empty;
    private string _libCsproj = string.Empty;
    private string _appCsproj = string.Empty;
    private string _testsCsproj = string.Empty;
    private string _libCode = string.Empty;

    public async ValueTask InitializeAsync()
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
        _ = await RunDotnetAsync("restore", _root);
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void EvaluatedBeforeRestore_IsSetOnlyWhileAProjectHasNoAssetsFile()
    {
        var restored = new MsBuildSolutionGraph(_slnPath);
        File.Delete(Path.Combine(_root, "Lib", "obj", "project.assets.json"));
        var unrestored = new MsBuildSolutionGraph(_slnPath);

        Assert.False(restored.EvaluatedBeforeRestore);
        Assert.True(unrestored.EvaluatedBeforeRestore);
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

        _ = Assert.Single(sut.TestProjectPaths);
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

    [Fact]
    public async Task MultiTargetedProject_FrameworkConditionalReference_IsTraversedBothWays()
    {
        // Multi.csproj targets netstandard2.0;net10.0 but references Net.csproj only for net10.0,
        // so the edge exists solely on one inner-build node.
        var dir = Directory.CreateDirectory(Path.Combine(_root, "multi")).FullName;
        var netCsproj = Path.Combine(dir, "Net", "Net.csproj");
        var multiCsproj = Path.Combine(dir, "Multi", "Multi.csproj");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(netCsproj)!);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(multiCsproj)!);

        await File.WriteAllTextAsync(netCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """, _ct);
        await File.WriteAllTextAsync(multiCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>
              </PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <ProjectReference Include="..\Net\Net.csproj" />
              </ItemGroup>
            </Project>
            """, _ct);
        var slnPath = Path.Combine(dir, "Multi.slnx");
        await File.WriteAllTextAsync(slnPath, """
            <Solution>
              <Project Path="Net/Net.csproj" />
              <Project Path="Multi/Multi.csproj" />
            </Solution>
            """, _ct);

        var sut = new MsBuildSolutionGraph(slnPath);

        Assert.Equal([Path.GetFullPath(netCsproj)], sut.GetTransitiveDependencies(multiCsproj));
        Assert.Equal([Path.GetFullPath(multiCsproj)], sut.GetTransitiveDependents(netCsproj));
    }

    [Theory]
    [InlineData("netstandard2.0")]
    [InlineData("net10.0")]
    public async Task MultiTargetedProject_ConditionalContainerReference_IsInferredAcrossAllNodes(string containerFramework)
    {
        var project = Path.Combine(_root, "Multi.Tests.csproj");
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == '{{containerFramework}}'">
                <PackageReference Include="Testcontainers.PostgreSql" Version="4.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' != '{{containerFramework}}'">
                <PackageReference Include="xunit" Version="2.9.0" />
              </ItemGroup>
            </Project>
            """, _ct);
        var solution = Path.Combine(_root, "Multi.slnx");
        await File.WriteAllTextAsync(solution, """<Solution><Project Path="Multi.Tests.csproj" /></Solution>""", _ct);

        var graph = new MsBuildSolutionGraph(solution);
        var facts = graph.GetTestProjectFacts(project);
        var needs = new TestResourceInference(solution, [], graph.GetTestProjectFacts).Resolve(project);

        Assert.NotNull(facts);
        Assert.Contains("Testcontainers.PostgreSql", facts.PackageReferences);
        Assert.Contains("xunit", facts.PackageReferences);
        Assert.True(needs.UsesContainers);
        Assert.StartsWith("project:", Assert.Single(needs.Keys), StringComparison.Ordinal);
    }

    private static Task<int> RunDotnetAsync(string args, string workDir) =>
        TestProcess.RunDotnetAsync(args, workDir);
}
