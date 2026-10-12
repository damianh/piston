// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Engine.Impact;
using Xunit;

namespace Piston.Engine.Tests.Impact;

public sealed class VerificationInputCoverageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("piston-input-coverage-").FullName;
    private readonly string _solution;
    private readonly string _project;

    public VerificationInputCoverageTests()
    {
        MsBuildLocatorGuard.EnsureRegistered();
        var directory = Directory.CreateDirectory(Path.Combine(_root, "solution")).FullName;
        _solution = Path.Combine(directory, "Test.slnx");
        _project = Path.Combine(directory, "Test.csproj");
        File.WriteAllText(_solution, """<Solution><Project Path="Test.csproj" /></Solution>""");
        File.WriteAllText(Path.Combine(directory, "Test.cs"), "class Test {}");
        File.WriteAllText(Path.Combine(directory, ".editorconfig"), "root = true\n");
        WriteProject("");
    }

    private void WriteProject(string extra) => File.WriteAllText(_project, $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          {{extra}}
        </Project>
        """);

    [Fact]
    public void InTreeInputsAndSdkImportsAreCovered()
    {
        var issue = new MsBuildSolutionGraph(_solution).VerificationCoverageIssue;
        Assert.True(issue is null, issue);
    }

    [Fact]
    public void ProjectReferenceOutsideSolutionIsNotCovered()
    {
        File.WriteAllText(Path.Combine(_root, "External.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        WriteProject("""<ItemGroup><ProjectReference Include="../External.csproj" /></ItemGroup>""");

        Assert.Contains("outside the source fingerprint", new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);
    }

    [Fact]
    public void ExternalImportIncludingTransitiveImportIsNotCovered()
    {
        File.WriteAllText(Path.Combine(_root, "External.targets"), "<Project />");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_project)!, "InTree.targets"),
            """<Project><Import Project="../External.targets" /></Project>""");
        WriteProject("""<Import Project="InTree.targets" />""");

        Assert.Contains("External.targets", new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);
    }

    [Fact]
    public void LinkedCompileFileOutsideSolutionIsNotCovered()
    {
        File.WriteAllText(Path.Combine(_root, "Linked.cs"), "class Linked {}");
        WriteProject("""<ItemGroup><Compile Include="../Linked.cs" /></ItemGroup>""");

        Assert.Contains("Linked.cs", new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);
    }

    [Fact]
    public void AncestorEditorConfigOutsideSolutionIsNotCovered()
    {
        File.Delete(Path.Combine(Path.GetDirectoryName(_solution)!, ".editorconfig"));
        var editorConfig = Path.Combine(_root, ".editorconfig");
        File.WriteAllText(editorConfig, "root = true\n");

        Assert.Contains(".editorconfig", new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);
    }

    [Fact]
    public void EditorConfigInsideSolutionIsCovered()
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_solution)!, ".editorconfig"), "root = true\n");

        Assert.Null(new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);
    }

    [Fact]
    public void UsingTaskAssemblyOutsideSolutionIsNotCovered()
    {
        var build = Directory.CreateDirectory(Path.Combine(_root, "build")).FullName;
        File.WriteAllBytes(Path.Combine(build, "Tasks.dll"), [1, 2, 3]);
        WriteProject("""<UsingTask TaskName="CustomTask" AssemblyFile="../build/Tasks.dll" />""");

        Assert.Contains("Tasks.dll", new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);
    }

    [Fact]
    public void UsingTaskAssemblyPathIsResolvedFromImportedTargetsDirectory()
    {
        var solutionDirectory = Path.GetDirectoryName(_solution)!;
        var nestedDirectory = Directory.CreateDirectory(Path.Combine(solutionDirectory, "nested")).FullName;
        var nestedProject = Path.Combine(nestedDirectory, "Test.csproj");
        File.WriteAllText(nestedProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(_solution, """<Solution><Project Path="nested/Test.csproj" /></Solution>""");
        var targets = Path.Combine(solutionDirectory, "Directory.Build.targets");
        File.WriteAllText(targets, """<Project><UsingTask TaskName="CustomTask" AssemblyFile="../build/Tasks.dll" /></Project>""");
        var externalBuild = Directory.CreateDirectory(Path.Combine(_root, "build")).FullName;
        File.WriteAllBytes(Path.Combine(externalBuild, "Tasks.dll"), [1, 2, 3]);
        _ = Directory.CreateDirectory(Path.Combine(solutionDirectory, "build"));

        var issue = new MsBuildSolutionGraph(_solution).VerificationCoverageIssue;

        Assert.Contains(Path.Combine(externalBuild, "Tasks.dll"), issue);
    }

    [Fact]
    public void RestoredPackageTargetsAreExcludedButUserImportsUnderPackageRootAreNot()
    {
        var packageRoot = Path.Combine(_root, "packages");
        var packageDirectory = Path.Combine(packageRoot, "sample.package", "1.0.0");
        var targetPath = Path.Combine(packageDirectory, "build", "Package.targets");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath, "<Project />");

        var extensions = Path.Combine(Path.GetDirectoryName(_project)!, "obj");
        _ = Directory.CreateDirectory(extensions);
        File.WriteAllText(Path.Combine(extensions, "Test.csproj.nuget.g.targets"),
            """<Project><Import Project="$(NuGetPackageRoot)sample.package/1.0.0/build/Package.targets" /></Project>""");
        File.WriteAllText(Path.Combine(extensions, "project.assets.json"), JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            libraries = new Dictionary<string, object>
            {
                ["sample.package/1.0.0"] = new { type = "package", path = "sample.package/1.0.0" },
            },
        }));

        var packageProperties = $$"""
            <PropertyGroup>
              <NuGetPackageRoot>{{packageRoot}}{{Path.DirectorySeparatorChar}}</NuGetPackageRoot>
              <MSBuildProjectExtensionsPath>{{extensions}}{{Path.DirectorySeparatorChar}}</MSBuildProjectExtensionsPath>
              <ProjectAssetsFile>{{Path.Combine(extensions, "project.assets.json")}}</ProjectAssetsFile>
            </PropertyGroup>
            """;
        WriteProject(packageProperties);

        Assert.Null(new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);

        WriteProject(packageProperties +
            """<Import Project="$(NuGetPackageRoot)sample.package/1.0.0/build/Package.targets" />""");
        Assert.Contains("cannot safely apply the package/targeting-pack exclusion",
            new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);
    }

    [Fact]
    public void InstalledWorkloadImportRequiresToolchainProvenance()
    {
        var dotnetRoot = Path.Combine(_root, "dotnet");
        var sdkRoot = Path.Combine(dotnetRoot, "sdk", "10.0.100");
        var userDotnetRoot = Path.Combine(_root, "user", ".dotnet");
        var workloadImport = Path.Combine(dotnetRoot, "packs", "Microsoft.Android.Sdk.Windows",
            "36.1.69", "Sdk", "AutoImport.props");

        Assert.True(VerificationInputCoverage.IsInstalledPackInput(workloadImport,
            Path.Combine(sdkRoot, "Sdks", "Microsoft.NET.Sdk", "targets", "Microsoft.NET.Sdk.ImportWorkloads.props"),
            sdkRoot, userDotnetRoot));
        Assert.False(VerificationInputCoverage.IsInstalledPackInput(workloadImport, _project, sdkRoot, userDotnetRoot));
        Assert.False(VerificationInputCoverage.IsInstalledPackInput(
            Path.Combine(dotnetRoot, "sdk", "packs", "User.props"), _project, sdkRoot, userDotnetRoot));
    }

    [Fact]
    public void RepositorySolutionCoverage()
    {
        var solution = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Piston.slnx"));
        var issue = new MsBuildSolutionGraph(solution).VerificationCoverageIssue;
        Assert.True(issue is null, issue);
    }

    [Theory]
    [InlineData("NuGetPackageRoot")]
    [InlineData("NetCoreTargetingPackRoot")]
    public void ExclusionRootContainingUserInputFailsClosed(string propertyName)
    {
        var externalDirectory = Directory.CreateDirectory(Path.Combine(_root, "external-inputs")).FullName;
        var source = Path.Combine(externalDirectory, "External.cs");
        File.WriteAllText(source, "class External {}");
        WriteProject($$"""
            <PropertyGroup>
              <{{propertyName}}>{{externalDirectory}}</{{propertyName}}>
            </PropertyGroup>
            <ItemGroup><Compile Include="../external-inputs/External.cs" /></ItemGroup>
            """);

        Assert.Contains("cannot safely apply the package/targeting-pack exclusion",
            new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);
    }

    [Theory]
    [InlineData("NuGetPackageRoot")]
    [InlineData("NetCoreTargetingPackRoot")]
    public void ExclusionRootContainingExternalProjectFailsClosed(string propertyName)
    {
        var externalDirectory = Directory.CreateDirectory(Path.Combine(_root, "external-inputs")).FullName;
        File.WriteAllText(Path.Combine(externalDirectory, "External.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        WriteProject($$"""
            <PropertyGroup>
              <{{propertyName}}>{{externalDirectory}}</{{propertyName}}>
            </PropertyGroup>
            <ItemGroup><ProjectReference Include="../external-inputs/External.csproj" /></ItemGroup>
            """);

        Assert.NotNull(new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);
    }

    [Fact]
    public void AncestorPropsAreCoveredButTheirExternalImportsAreNot()
    {
        var props = Path.Combine(_root, "Directory.Build.props");
        File.WriteAllText(props, "<Project />");
        Assert.Null(new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);
        File.WriteAllText(Path.Combine(_root, "External.targets"), "<Project />");
        File.WriteAllText(props, """<Project><Import Project="External.targets" /></Project>""");

        Assert.Contains("External.targets", new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);
    }

    [Theory]
    [InlineData("Content", "obj")]
    [InlineData("None", "obj")]
    [InlineData("EmbeddedResource", "obj")]
    [InlineData("Content", "bin")]
    [InlineData("None", "bin")]
    [InlineData("EmbeddedResource", "bin")]
    public void ExplicitDataInputInExcludedDirectoryIsNotCovered(string itemType, string excludedDirectory)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(_project)!, excludedDirectory));
        File.WriteAllText(Path.Combine(directory.FullName, "config.json"), "{}");
        WriteProject($"""<ItemGroup><{itemType} Include="{excludedDirectory}/config.json" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>""");

        Assert.Contains("config.json", new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);
    }

    [Fact]
    public void RuntimeConfigProducedByReferencedProjectIsCovered()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(_project)!, "Host"));
        File.WriteAllText(Path.Combine(directory.FullName, "Host.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <GenerateRuntimeConfigurationFiles>true</GenerateRuntimeConfigurationFiles>
              </PropertyGroup>
            </Project>
            """);
        var outputDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "bin", "Debug", "net10.0"));
        File.WriteAllText(Path.Combine(outputDirectory.FullName, "Host.runtimeconfig.json"), "{}");
        WriteProject("""
            <ItemGroup>
              <ProjectReference Include="Host/Host.csproj" />
              <None Include="Host/bin/Debug/net10.0/Host.runtimeconfig.json" CopyToOutputDirectory="PreserveNewest" />
            </ItemGroup>
            """);

        var issue = new MsBuildSolutionGraph(_solution).VerificationCoverageIssue;
        Assert.True(issue is null, issue);
    }

    [Theory]
    [InlineData(".piston")]
    [InlineData("obj")]
    public void ExplicitSourceInExcludedDirectoryIsNotCovered(string directory)
    {
        var hidden = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(_project)!, directory));
        File.WriteAllText(Path.Combine(hidden.FullName, "Hidden.cs"), "class Hidden {}");
        WriteProject($"""<ItemGroup><Compile Include="{directory}/Hidden.cs" /></ItemGroup>""");

        Assert.Contains("Hidden.cs", new MsBuildSolutionGraph(_solution).VerificationCoverageIssue);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
