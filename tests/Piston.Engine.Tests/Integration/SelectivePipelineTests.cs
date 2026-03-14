using Piston.Engine.Impact;
using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.Integration;

/// <summary>
/// End-to-end integration test for the selective pipeline.
/// Verifies that changing a file in one project only affects that project and
/// its test project, leaving the unrelated project untouched.
/// </summary>
/// <remarks>
/// Solution layout:
///   Lib/Lib.csproj            (source project)
///   Lib.Tests/Lib.Tests.csproj (test project, references Lib, IsTestProject=true)
///   Other/Other.csproj         (unrelated source project)
///   Other.Tests/Other.Tests.csproj (test project, references Other, IsTestProject=true)
/// </remarks>
public sealed class SelectivePipelineTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private string _slnPath = string.Empty;
    private string _libCsproj = string.Empty;
    private string _libTestsCsproj = string.Empty;
    private string _otherCsproj = string.Empty;
    private string _otherTestsCsproj = string.Empty;
    private string _libCodeFile = string.Empty;

    public async Task InitializeAsync()
    {
        MsBuildLocatorGuard.EnsureRegistered();

        _root = Directory.CreateTempSubdirectory("piston-selective-pipeline-").FullName;

        var libDir = Directory.CreateDirectory(Path.Combine(_root, "Lib")).FullName;
        var libTestsDir = Directory.CreateDirectory(Path.Combine(_root, "Lib.Tests")).FullName;
        var otherDir = Directory.CreateDirectory(Path.Combine(_root, "Other")).FullName;
        var otherTestsDir = Directory.CreateDirectory(Path.Combine(_root, "Other.Tests")).FullName;

        _libCsproj = Path.Combine(libDir, "Lib.csproj");
        _libTestsCsproj = Path.Combine(libTestsDir, "Lib.Tests.csproj");
        _otherCsproj = Path.Combine(otherDir, "Other.csproj");
        _otherTestsCsproj = Path.Combine(otherTestsDir, "Other.Tests.csproj");
        _libCodeFile = Path.Combine(libDir, "Code.cs");

        await File.WriteAllTextAsync(_libCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(_libCodeFile, "namespace Lib; public class LibCode {}");

        await File.WriteAllTextAsync(_libTestsCsproj, """
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
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(Path.Combine(libTestsDir, "LibTests.cs"), """
            using Xunit;
            namespace Lib.Tests;
            public class LibTests { [Fact] public void Passes() => Assert.True(true); }
            """);

        await File.WriteAllTextAsync(_otherCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(Path.Combine(otherDir, "OtherCode.cs"), "namespace Other; public class OtherCode {}");

        await File.WriteAllTextAsync(_otherTestsCsproj, """
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
                <ProjectReference Include="..\Other\Other.csproj" />
              </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(Path.Combine(otherTestsDir, "OtherTests.cs"), """
            using Xunit;
            namespace Other.Tests;
            public class OtherTests { [Fact] public void Passes() => Assert.True(true); }
            """);

        _slnPath = Path.Combine(_root, "Test.slnx");
        await File.WriteAllTextAsync(_slnPath, $"""
            <Solution>
              <Project Path="Lib/Lib.csproj" />
              <Project Path="Lib.Tests/Lib.Tests.csproj" />
              <Project Path="Other/Other.csproj" />
              <Project Path="Other.Tests/Other.Tests.csproj" />
            </Solution>
            """);

        await RunDotnetAsync("restore", _root);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ChangeInLib_AffectsOnlyLibAndLibTests()
    {
        var analyzer = new ImpactAnalyzer(path => new MsBuildSolutionGraph(path));
        await analyzer.InitializeAsync(_slnPath, CancellationToken.None);

        var changes = new[]
        {
            new FileChangeEvent(_libCodeFile, WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)
        };

        var result = analyzer.Analyze(changes);

        Assert.False(result.IsFullRun);

        // Lib.csproj must be in the build targets
        Assert.Contains(result.AffectedProjectPaths,
            p => p.EndsWith("Lib.csproj", StringComparison.OrdinalIgnoreCase));

        // Lib.Tests.csproj must be in the test targets
        Assert.Contains(result.AffectedTestProjectPaths,
            p => p.EndsWith("Lib.Tests.csproj", StringComparison.OrdinalIgnoreCase));

        // Other.csproj must NOT be in the build targets
        Assert.DoesNotContain(result.AffectedProjectPaths,
            p => p.EndsWith("Other.csproj", StringComparison.OrdinalIgnoreCase));

        // Other.Tests.csproj must NOT be in the test targets
        Assert.DoesNotContain(result.AffectedTestProjectPaths,
            p => p.EndsWith("Other.Tests.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FullRun_IsFullRunTrue_NoAffectedPaths()
    {
        var analyzer = new ImpactAnalyzer(path => new MsBuildSolutionGraph(path));
        await analyzer.InitializeAsync(_slnPath, CancellationToken.None);

        var result = analyzer.AnalyzeFullRun();

        Assert.True(result.IsFullRun);
        Assert.Empty(result.AffectedProjectPaths);
        Assert.Empty(result.AffectedTestProjectPaths);
    }

    [Fact]
    public async Task ChangeInLibTests_AffectsOnlyLibTests_NotBuildTargets()
    {
        var analyzer = new ImpactAnalyzer(path => new MsBuildSolutionGraph(path));
        await analyzer.InitializeAsync(_slnPath, CancellationToken.None);

        var testFile = Path.Combine(Path.GetDirectoryName(_libTestsCsproj)!, "LibTests.cs");

        var changes = new[]
        {
            new FileChangeEvent(testFile, WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)
        };

        var result = analyzer.Analyze(changes);

        Assert.False(result.IsFullRun);

        // Only the test project is a test target — no source build needed
        Assert.Contains(result.AffectedTestProjectPaths,
            p => p.EndsWith("Lib.Tests.csproj", StringComparison.OrdinalIgnoreCase));

        // Other projects must not be affected
        Assert.DoesNotContain(result.AffectedTestProjectPaths,
            p => p.EndsWith("Other.Tests.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.AffectedProjectPaths,
            p => p.EndsWith("Other.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoDuplicatesInAffectedPaths_WhenMultipleFilesInSameProject()
    {
        var analyzer = new ImpactAnalyzer(path => new MsBuildSolutionGraph(path));
        await analyzer.InitializeAsync(_slnPath, CancellationToken.None);

        var libDir = Path.GetDirectoryName(_libCodeFile)!;
        var secondFile = Path.Combine(libDir, "Another.cs");
        await File.WriteAllTextAsync(secondFile, "namespace Lib; public class Another {}");

        var changes = new[]
        {
            new FileChangeEvent(_libCodeFile, WatcherChangeTypes.Changed, DateTimeOffset.UtcNow),
            new FileChangeEvent(secondFile, WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)
        };

        var result = analyzer.Analyze(changes);

        Assert.False(result.IsFullRun);

        // Build targets should not contain duplicate Lib.csproj entries
        var libCsprojCount = result.AffectedProjectPaths
            .Count(p => p.EndsWith("Lib.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, libCsprojCount);
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
