// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

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
