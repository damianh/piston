// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Integration;

public sealed class WindowsBuildPathTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("slnx", false)]
    [InlineData("sln", false)]
    [InlineData("slnf", false)]
    [InlineData("slnx", true)]
    public async Task BothPathForms_ResolveParentReferences_InGraphAndBuilds(string format, bool longPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        MsBuildLocatorGuard.EnsureRegistered();
        var temporary = Directory.CreateTempSubdirectory("piston-windows-path-").FullName;
        try
        {
            var root = temporary;
            if (longPath)
            {
                while (root.Length <= 280)
                {
                    root = Path.Combine(root, "nested-directory");
                }
                Assert.True(root.Length > 260);
            }

            var libDir = Directory.CreateDirectory(Path.Combine(root, "lib")).FullName;
            var appDir = Directory.CreateDirectory(Path.Combine(root, "src", "App")).FullName;
            var solutionDir = Directory.CreateDirectory(Path.Combine(root, "solutions", "inner")).FullName;
            var lib = Path.Combine(libDir, "Lib.csproj");
            var app = Path.Combine(appDir, "App.csproj");
            await File.WriteAllTextAsync(lib, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """, _ct);
            await File.WriteAllTextAsync(Path.Combine(libDir, "Value.cs"),
                "namespace Lib; public class Value {}", _ct);
            await File.WriteAllTextAsync(app, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="..\..\lib\Lib.csproj" /></ItemGroup>
                </Project>
                """, _ct);
            await File.WriteAllTextAsync(Path.Combine(appDir, "App.cs"),
                "public class App { public Lib.Value Value = new(); }", _ct);

            var solution = Path.Combine(solutionDir, "App.slnx");
            await File.WriteAllTextAsync(solution, """
                <Solution><Project Path="../../src/App/App.csproj" /></Solution>
                """, _ct);
            if (format == "sln")
            {
                File.Delete(solution);
                solution = Path.Combine(solutionDir, "App.sln");
                await File.WriteAllTextAsync(solution, """
                    Microsoft Visual Studio Solution File, Format Version 12.00
                    # Visual Studio Version 17
                    Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "..\..\src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
                    EndProject
                    Global
                        GlobalSection(SolutionConfigurationPlatforms) = preSolution
                            Debug|Any CPU = Debug|Any CPU
                        EndGlobalSection
                        GlobalSection(ProjectConfigurationPlatforms) = postSolution
                            {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                            {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
                        EndGlobalSection
                    EndGlobal
                    """, _ct);
            }
            else if (format == "slnf")
            {
                solution = Path.Combine(solutionDir, "App.slnf");
                await File.WriteAllTextAsync(solution, """
                    {"solution":{"path":"App.slnx","projects":["..\\..\\src\\App\\App.csproj"]}}
                    """, _ct);
            }

            _ = await TestProcess.RunDotnetAsync($"restore \"{solution}\"", PathNormalizer.GetSdkWorkingDirectory(solutionDir));
            foreach (var input in new[] { solution, @"\\?\" + solution })
            {
                var graph = new MsBuildSolutionGraph(input);
                Assert.Equal(2, graph.AllProjectPaths.Count);
                Assert.Contains(lib, graph.GetTransitiveDependencies(@"\\?\" + app));
                Assert.Equal(app, graph.FindOwningProject(@"\\?\" + Path.Combine(appDir, "App.cs")));

                var build = new BuildService();
                var full = await build.BuildAsync(input, _ct);
                Assert.True(full.Status == BuildStatus.Succeeded, string.Join(Environment.NewLine, full.Errors));
                var appOutput = Path.Combine(appDir, "bin", "Debug", "net10.0", "App.dll");
                var libOutput = Path.Combine(libDir, "bin", "Debug", "net10.0", "Lib.dll");
                Assert.True(File.Exists(appOutput));
                Assert.True(File.Exists(libOutput));
                File.Delete(appOutput);
                File.Delete(libOutput);

                // App is a solution member; Lib is reached only through ProjectReference.
                // Mixed spellings exercise filter membership and the outside-project build.
                var selective = await build.BuildAsync(input, [@"\\?\" + app, lib], _ct);
                Assert.True(selective.Status == BuildStatus.Succeeded, string.Join(Environment.NewLine, selective.Errors));
                Assert.True(File.Exists(appOutput));
                Assert.True(File.Exists(libOutput));
            }
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
}
