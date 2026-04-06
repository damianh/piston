using Piston.Engine.Impact;
using Xunit;

namespace Piston.Engine.Tests.Impact;

/// <summary>
/// Integration tests for MTP v2 project detection in <see cref="MsBuildSolutionGraph"/>.
/// Verifies that each MSBuild property / PackageReference used by MTP-enabled frameworks
/// is correctly detected during graph construction.
/// </summary>
public sealed class MtpDetectionTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private string _slnPath = string.Empty;
    private string _mtpCsproj = string.Empty;
    private string _vsTestCsproj = string.Empty;
    private string _libCsproj = string.Empty;

    public async Task InitializeAsync()
    {
        MsBuildLocatorGuard.EnsureRegistered();

        _root = Directory.CreateTempSubdirectory("piston-mtp-detection-").FullName;

        var mtpDir    = Directory.CreateDirectory(Path.Combine(_root, "MtpProject")).FullName;
        var vsTestDir = Directory.CreateDirectory(Path.Combine(_root, "VsTestProject")).FullName;
        var libDir    = Directory.CreateDirectory(Path.Combine(_root, "Lib")).FullName;

        _mtpCsproj    = Path.Combine(mtpDir, "MtpProject.csproj");
        _vsTestCsproj = Path.Combine(vsTestDir, "VsTestProject.csproj");
        _libCsproj    = Path.Combine(libDir, "Lib.csproj");

        // Lib: plain class library, not a test project
        await File.WriteAllTextAsync(_libCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(libDir, "Code.cs"), "namespace Lib; public class Code {}");

        // VsTestProject: standard VSTest project
        await File.WriteAllTextAsync(_vsTestCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(vsTestDir, "Tests.cs"),
            "namespace VsTestProject; public class Tests {}");

        // MtpProject: placeholder; actual content set per-test in helpers below
        await File.WriteAllTextAsync(_mtpCsproj, BuildMtpCsproj("<IsTestingPlatformApplication>true</IsTestingPlatformApplication>"));
        await File.WriteAllTextAsync(Path.Combine(mtpDir, "Tests.cs"),
            "namespace MtpProject; public class Tests {}");

        _slnPath = Path.Combine(_root, "Test.slnx");
        await WriteSolution();
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IsMtpProject_IsTestingPlatformApplication_ReturnsTrue()
    {
        await SetMtpProjectContent("<IsTestingPlatformApplication>true</IsTestingPlatformApplication>");
        var sut = new MsBuildSolutionGraph(_slnPath);
        Assert.True(sut.IsMtpProject(_mtpCsproj));
    }

    [Fact]
    public async Task IsMtpProject_EnableMSTestRunner_ReturnsTrue()
    {
        await SetMtpProjectContent("<EnableMSTestRunner>true</EnableMSTestRunner>");
        var sut = new MsBuildSolutionGraph(_slnPath);
        Assert.True(sut.IsMtpProject(_mtpCsproj));
    }

    [Fact]
    public async Task IsMtpProject_UseMicrosoftTestingPlatformRunner_ReturnsTrue()
    {
        await SetMtpProjectContent("<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>");
        var sut = new MsBuildSolutionGraph(_slnPath);
        Assert.True(sut.IsMtpProject(_mtpCsproj));
    }

    [Fact]
    public async Task IsMtpProject_EnableNUnitRunner_ReturnsTrue()
    {
        await SetMtpProjectContent("<EnableNUnitRunner>true</EnableNUnitRunner>");
        var sut = new MsBuildSolutionGraph(_slnPath);
        Assert.True(sut.IsMtpProject(_mtpCsproj));
    }

    [Fact]
    public async Task IsMtpProject_MicrosoftTestingPlatformPackageReference_ReturnsTrue()
    {
        await SetMtpProjectContent(null, packageRef: "Microsoft.Testing.Platform");
        var sut = new MsBuildSolutionGraph(_slnPath);
        Assert.True(sut.IsMtpProject(_mtpCsproj));
    }

    [Fact]
    public async Task IsMtpProject_VsTestProject_ReturnsFalse()
    {
        await SetMtpProjectContent("<IsTestingPlatformApplication>true</IsTestingPlatformApplication>");
        var sut = new MsBuildSolutionGraph(_slnPath);
        Assert.False(sut.IsMtpProject(_vsTestCsproj));
    }

    [Fact]
    public async Task IsMtpProject_PlainLibrary_ReturnsFalse()
    {
        await SetMtpProjectContent("<IsTestingPlatformApplication>true</IsTestingPlatformApplication>");
        var sut = new MsBuildSolutionGraph(_slnPath);
        Assert.False(sut.IsMtpProject(_libCsproj));
    }

    [Fact]
    public async Task GetMtpOutputPath_MtpProject_ReturnsNonNullPath()
    {
        await SetMtpProjectContent("<IsTestingPlatformApplication>true</IsTestingPlatformApplication>");
        var sut = new MsBuildSolutionGraph(_slnPath);
        var path = sut.GetMtpOutputPath(_mtpCsproj);
        Assert.NotNull(path);
        Assert.EndsWith(".dll", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetMtpOutputPath_VsTestProject_ReturnsNull()
    {
        await SetMtpProjectContent("<IsTestingPlatformApplication>true</IsTestingPlatformApplication>");
        var sut = new MsBuildSolutionGraph(_slnPath);
        Assert.Null(sut.GetMtpOutputPath(_vsTestCsproj));
    }

    [Fact]
    public async Task GetMtpOutputPath_IsFullPath()
    {
        await SetMtpProjectContent("<IsTestingPlatformApplication>true</IsTestingPlatformApplication>");
        var sut = new MsBuildSolutionGraph(_slnPath);
        var path = sut.GetMtpOutputPath(_mtpCsproj);
        Assert.NotNull(path);
        Assert.True(Path.IsPathRooted(path), "MTP output path should be a full (rooted) path");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SetMtpProjectContent(string? property, string? packageRef = null)
    {
        var content = BuildMtpCsproj(property, packageRef);
        await File.WriteAllTextAsync(_mtpCsproj, content);
        // Re-write solution to ensure it picks up the updated project
        await WriteSolution();
    }

    private Task WriteSolution() =>
        File.WriteAllTextAsync(_slnPath, $"""
            <Solution>
              <Project Path="Lib/Lib.csproj" />
              <Project Path="VsTestProject/VsTestProject.csproj" />
              <Project Path="MtpProject/MtpProject.csproj" />
            </Solution>
            """);

    private static string BuildMtpCsproj(string? property, string? packageRef = null)
    {
        var propBlock = property is not null ? $"    {property}" : string.Empty;
        var pkgBlock  = packageRef is not null
            ? $"""
              <ItemGroup>
                <PackageReference Include="{packageRef}" Version="1.0.0" />
              </ItemGroup>
            """
            : string.Empty;

        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
            {propBlock}
              </PropertyGroup>
            {pkgBlock}
            </Project>
            """;
    }
}
