using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Piston.Engine.Tests.Orchestration;
using Xunit;

namespace Piston.Engine.Tests.Integration;

/// <summary>
/// End-to-end regression for selective build correctness. Test runners use
/// <c>--no-build</c>, so a selective build must rebuild affected test projects; otherwise
/// a stale copy of an edited library stays in the test output and tests report stale results.
/// </summary>
/// <remarks>
/// Uses the real impact analyzer, build service and VSTest runner against:
///   Lib/Lib.csproj                  (Lib.C.V constant)
///   Lib.Tests/Lib.Tests.csproj      (asserts Lib.C.V)
///   Other/Other.csproj
///   Other.Tests/Other.Tests.csproj  (unrelated)
/// </remarks>
public sealed class SelectiveBuildCorrectnessTests : IAsyncLifetime
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(4);

    private string _root = string.Empty;
    private string _libCode = string.Empty;
    private string _libTestCode = string.Empty;
    private string _libTestsCsproj = string.Empty;
    private string _otherTestsCsproj = string.Empty;

    public async Task InitializeAsync()
    {
        MsBuildLocatorGuard.EnsureRegistered();
        _root = Directory.CreateTempSubdirectory("piston-selective-build-").FullName;

        var libDir = Directory.CreateDirectory(Path.Combine(_root, "Lib")).FullName;
        var libTestsDir = Directory.CreateDirectory(Path.Combine(_root, "Lib.Tests")).FullName;
        var otherDir = Directory.CreateDirectory(Path.Combine(_root, "Other")).FullName;
        var otherTestsDir = Directory.CreateDirectory(Path.Combine(_root, "Other.Tests")).FullName;

        _libCode = Path.Combine(libDir, "C.cs");
        _libTestCode = Path.Combine(libTestsDir, "LibTests.cs");
        _libTestsCsproj = Path.Combine(libTestsDir, "Lib.Tests.csproj");
        _otherTestsCsproj = Path.Combine(otherTestsDir, "Other.Tests.csproj");

        const string libProject = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;

        static string TestProject(string reference) => $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
                <PackageReference Include="xunit" Version="2.*" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
                <ProjectReference Include="{reference}" />
              </ItemGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(Path.Combine(libDir, "Lib.csproj"), libProject);
        await File.WriteAllTextAsync(_libCode, LibSource(1));
        await File.WriteAllTextAsync(_libTestsCsproj, TestProject(@"..\Lib\Lib.csproj"));
        await File.WriteAllTextAsync(_libTestCode, LibTestSource(1));

        await File.WriteAllTextAsync(Path.Combine(otherDir, "Other.csproj"), libProject);
        await File.WriteAllTextAsync(Path.Combine(otherDir, "O.cs"), "namespace Other; public static class O {}");
        await File.WriteAllTextAsync(_otherTestsCsproj, TestProject(@"..\Other\Other.csproj"));
        await File.WriteAllTextAsync(Path.Combine(otherTestsDir, "OtherTests.cs"), """
            namespace Other.Tests;
            public class OtherTests { [Xunit.Fact] public void Passes() => Xunit.Assert.True(true); }
            """);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("slnx")]
    [InlineData("sln")]
    [InlineData("slnf")]
    public async Task ReferencedLibraryEdit_RebuildsTestProject_AndChangesObservedOutcome(string format)
    {
        var solutionPath = await WriteSolutionAsync(format);

        var state = new PistonState();
        var watcher = new StubFileWatcherService();
        var build = new RecordingBuildService(new BuildService());
        var runner = new TestRunnerService(new ProcessTestExecutionStrategy(new TrxResultParser()));
        var analyzer = new ImpactAnalyzer(path => new MsBuildSolutionGraph(path));

        using var orchestrator = new PistonOrchestrator(watcher, build, runner, analyzer, state);

        // Initial full run: Lib.C.V == 1, test expects 1.
        await RunAndWaitAsync(state, () => orchestrator.StartAsync(solutionPath));
        Assert.Equal(BuildStatus.Succeeded, state.LastBuild?.Status);
        Assert.True(LibTestStatus(state) == TestStatus.Passed, Describe(state));
        Assert.Null(build.Calls[^1]); // full run builds the whole solution

        // Edit the referenced library only. The test must now fail.
        await File.WriteAllTextAsync(_libCode, LibSource(2));
        await RunAndWaitAsync(state, () => TriggerChange(watcher, _libCode));

        var libEditTargets = build.Calls[^1];
        Assert.NotNull(libEditTargets);
        Assert.Contains(libEditTargets, p => SamePath(p, _libTestsCsproj));
        Assert.DoesNotContain(libEditTargets, p => SamePath(p, _otherTestsCsproj));
        Assert.Equal(BuildStatus.Succeeded, state.LastBuild?.Status);
        Assert.True(LibTestStatus(state) == TestStatus.Failed, Describe(state));

        // Test-only edit: selective build of just the test project, which now passes.
        await File.WriteAllTextAsync(_libTestCode, LibTestSource(2));
        await RunAndWaitAsync(state, () => TriggerChange(watcher, _libTestCode));

        var testEditTargets = build.Calls[^1];
        Assert.NotNull(testEditTargets);
        Assert.Contains(testEditTargets, p => SamePath(p, _libTestsCsproj));
        Assert.DoesNotContain(testEditTargets, p => SamePath(p, _otherTestsCsproj));
        Assert.True(LibTestStatus(state) == TestStatus.Passed, Describe(state));

        // Results from the untouched test project are preserved.
        Assert.Contains(state.TestSuites.SelectMany(s => s.Tests),
            t => t.FullyQualifiedName.Contains("OtherTests.Passes") && t.Status == TestStatus.Passed);
    }

    private async Task<string> WriteSolutionAsync(string format)
    {
        var slnx = Path.Combine(_root, "All.slnx");
        await File.WriteAllTextAsync(slnx, """
            <Solution>
              <Project Path="Lib/Lib.csproj" />
              <Project Path="Lib.Tests/Lib.Tests.csproj" />
              <Project Path="Other/Other.csproj" />
              <Project Path="Other.Tests/Other.Tests.csproj" />
            </Solution>
            """);

        switch (format)
        {
            case "slnx":
                return slnx;
            case "slnf":
            {
                var slnf = Path.Combine(_root, "Filtered.slnf");
                await File.WriteAllTextAsync(slnf, """
                    {"solution":{"path":"All.slnx","projects":[
                      "Lib\\Lib.csproj","Lib.Tests\\Lib.Tests.csproj",
                      "Other\\Other.csproj","Other.Tests\\Other.Tests.csproj"]}}
                    """);
                return slnf;
            }
            default:
            {
                File.Delete(slnx);
                var sln = Path.Combine(_root, "All.sln");
                await File.WriteAllTextAsync(sln, """

                    Microsoft Visual Studio Solution File, Format Version 12.00
                    # Visual Studio Version 17
                    Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
                    EndProject
                    Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib.Tests", "Lib.Tests\Lib.Tests.csproj", "{22222222-2222-2222-2222-222222222222}"
                    EndProject
                    Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Other", "Other\Other.csproj", "{33333333-3333-3333-3333-333333333333}"
                    EndProject
                    Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Other.Tests", "Other.Tests\Other.Tests.csproj", "{44444444-4444-4444-4444-444444444444}"
                    EndProject
                    Global
                    	GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    		Debug|Any CPU = Debug|Any CPU
                    	EndGlobalSection
                    	GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
                    		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
                    		{33333333-3333-3333-3333-333333333333}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    		{33333333-3333-3333-3333-333333333333}.Debug|Any CPU.Build.0 = Debug|Any CPU
                    		{44444444-4444-4444-4444-444444444444}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    		{44444444-4444-4444-4444-444444444444}.Debug|Any CPU.Build.0 = Debug|Any CPU
                    	EndGlobalSection
                    EndGlobal
                    """);
                return sln;
            }
        }
    }

    private static string LibSource(int value) =>
        $"namespace Lib; public static class C {{ public const int V = {value}; public static int Get() => V; }}";

    private static string LibTestSource(int expected) => $$"""
        namespace Lib.Tests;
        public class LibTests { [Xunit.Fact] public void V_matches() => Xunit.Assert.Equal({{expected}}, Lib.C.Get()); }
        """;

    private static TestStatus? LibTestStatus(PistonState state) =>
        state.TestSuites
            .SelectMany(s => s.Tests)
            .FirstOrDefault(t => t.FullyQualifiedName.Contains("LibTests.V_matches"))
            ?.Status;

    private static Task TriggerChange(StubFileWatcherService watcher, string path)
    {
        watcher.TriggerChange(new FileChangeBatch(
            [new FileChangeEvent(path, WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    private static async Task RunAndWaitAsync(PistonState state, Func<Task> trigger)
    {
        var previousRun = state.LastRunTime;
        var previousBuild = state.LastBuild;
        await trigger();

        var deadline = DateTime.UtcNow + RunTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (state.Phase == PistonPhase.Error && !ReferenceEquals(state.LastBuild, previousBuild))
                Assert.Fail("Build failed: " + string.Join(Environment.NewLine, state.LastBuild!.Errors));

            if (state.Phase == PistonPhase.Watching && state.LastRunTime != previousRun)
                return;

            await Task.Delay(100);
        }

        Assert.Fail($"Run did not complete within {RunTimeout}. Phase={state.Phase}");
    }

    private static string Describe(PistonState state) =>
        string.Join(Environment.NewLine, state.TestSuites.SelectMany(s =>
            s.Tests.Select(t => $"{s.Name}: {t.FullyQualifiedName} = {t.Status} {t.ErrorMessage}")))
        + Environment.NewLine + state.LastTestRunnerError;

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private sealed class RecordingBuildService(IBuildService inner) : IBuildService
    {
        public List<IReadOnlyList<string>?> Calls { get; } = [];

        public Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct) =>
            BuildAsync(solutionPath, null, ct);

        public Task<BuildResult> BuildAsync(string solutionPath, IReadOnlyList<string>? projectPaths, CancellationToken ct)
        {
            lock (Calls) Calls.Add(projectPaths);
            return inner.BuildAsync(solutionPath, projectPaths, ct);
        }
    }
}
