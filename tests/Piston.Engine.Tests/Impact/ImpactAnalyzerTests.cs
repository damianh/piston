using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Impact;

public sealed class ImpactAnalyzerTests : IDisposable
{
    // ── Test fixture: temp directory with a synthetic project layout ──────────
    //
    //   Root/
    //     Lib/
    //       Lib.csproj
    //       Code.cs
    //     App/
    //       App.csproj
    //       Program.cs
    //     App.Tests/
    //       App.Tests.csproj
    //       AppTests.cs

    private readonly string _root;
    private readonly string _libCsproj;
    private readonly string _appCsproj;
    private readonly string _testsCsproj;
    private readonly string _libCode;
    private readonly string _appProgram;
    private readonly string _testsFile;
    private readonly StubSolutionGraph _graph;
    private readonly Func<string, ISolutionGraph> _factory;

    public ImpactAnalyzerTests()
    {
        _root = Directory.CreateTempSubdirectory("piston-impact-test-").FullName;

        // Create project directories and files
        var libDir = Directory.CreateDirectory(Path.Combine(_root, "Lib")).FullName;
        var appDir = Directory.CreateDirectory(Path.Combine(_root, "App")).FullName;
        var testsDir = Directory.CreateDirectory(Path.Combine(_root, "App.Tests")).FullName;

        _libCsproj = Path.Combine(libDir, "Lib.csproj");
        _appCsproj = Path.Combine(appDir, "App.csproj");
        _testsCsproj = Path.Combine(testsDir, "App.Tests.csproj");
        _libCode = Path.Combine(libDir, "Code.cs");
        _appProgram = Path.Combine(appDir, "Program.cs");
        _testsFile = Path.Combine(testsDir, "AppTests.cs");

        // Create placeholder files so Tier 1 heuristic finds the csproj
        File.WriteAllText(_libCsproj, "<Project />");
        File.WriteAllText(_appCsproj, "<Project />");
        File.WriteAllText(_testsCsproj, "<Project />");
        File.WriteAllText(_libCode, "// lib");
        File.WriteAllText(_appProgram, "// app");
        File.WriteAllText(_testsFile, "// tests");

        // Stub graph: App.Tests depends on App, App depends on Lib
        _graph = new StubSolutionGraph(
            allProjects: [_libCsproj, _appCsproj, _testsCsproj],
            testProjects: [_testsCsproj],
            transitiveDependents: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [_libCsproj] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _appCsproj, _testsCsproj },
                [_appCsproj] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _testsCsproj },
                [_testsCsproj] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            },
            fileToProject: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [_libCode]    = _libCsproj,
                [_appProgram] = _appCsproj,
                [_testsFile]  = _testsCsproj,
            }
        );

        _factory = _ => _graph;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IImpactAnalyzer CreateInitialized()
    {
        var analyzer = new ImpactAnalyzer(_factory);
        analyzer.InitializeAsync("solution.slnx", CancellationToken.None).GetAwaiter().GetResult();
        return analyzer;
    }

    private static FileChangeEvent Change(string path) =>
        new(path, WatcherChangeTypes.Changed, DateTimeOffset.UtcNow);

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SingleCsFileInSourceProject_BuildsThatProject_RunsDependentTestProjects()
    {
        var analyzer = new ImpactAnalyzer(_factory);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        var result = analyzer.Analyze([Change(_libCode)]);

        Assert.False(result.IsFullRun);
        Assert.Contains(_libCsproj, result.AffectedProjectPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(_testsCsproj, result.AffectedTestProjectPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(_testsCsproj, result.AffectedProjectPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CsFileInTestProject_RunsOnlyThatTestProject_NoBuildTargets()
    {
        var analyzer = new ImpactAnalyzer(_factory);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        var result = analyzer.Analyze([Change(_testsFile)]);

        Assert.False(result.IsFullRun);
        Assert.Contains(_testsCsproj, result.AffectedTestProjectPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(result.AffectedProjectPaths);
    }

    [Fact]
    public async Task MultipleCsFilesInSameProject_DeduplicatesToOneOwningProject()
    {
        // Create a second .cs file in the same Lib project
        var libCode2 = Path.Combine(Path.GetDirectoryName(_libCode)!, "Code2.cs");
        File.WriteAllText(libCode2, "// lib2");

        var analyzer = new ImpactAnalyzer(_factory);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        var result = analyzer.Analyze([Change(_libCode), Change(libCode2)]);

        Assert.False(result.IsFullRun);
        // Both files are in Lib — the owning project deduplicates to one unique source project
        // (App.csproj also appears because it's a transitive dependent of Lib.csproj)
        Assert.Contains(_libCsproj, result.AffectedProjectPaths, StringComparer.OrdinalIgnoreCase);
        // Only one entry for Lib.csproj — no duplicates
        Assert.Equal(
            result.AffectedProjectPaths.Count,
            result.AffectedProjectPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task MultipleCsFilesAcrossDifferentProjects_UnionOfAffectedTestProjects()
    {
        var analyzer = new ImpactAnalyzer(_factory);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        // Change both Lib/Code.cs and App/Program.cs
        var result = analyzer.Analyze([Change(_libCode), Change(_appProgram)]);

        Assert.False(result.IsFullRun);
        // Both source projects should be in the build list
        Assert.Contains(_libCsproj, result.AffectedProjectPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(_appCsproj, result.AffectedProjectPaths, StringComparer.OrdinalIgnoreCase);
        // App.Tests depends on both
        Assert.Contains(_testsCsproj, result.AffectedTestProjectPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CsprojChange_SetsRequiresGraphRebuild_AndIncludesProjectAndDependents()
    {
        var analyzer = new ImpactAnalyzer(_factory);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        var result = analyzer.Analyze([Change(_libCsproj)]);

        Assert.True(result.RequiresGraphRebuild);
        Assert.Contains(_libCsproj, result.AffectedProjectPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FileWithNoOwningProject_FallsBackToFullRun()
    {
        var analyzer = new ImpactAnalyzer(_factory);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        // A loose .cs file at a path with no .csproj in the tree
        // Use a temp dir that has no .csproj
        var looseDir = Directory.CreateTempSubdirectory("piston-loose-").FullName;
        var looseFile = Path.Combine(looseDir, "Loose.cs");
        File.WriteAllText(looseFile, "// loose");

        try
        {
            var result = analyzer.Analyze([Change(looseFile)]);
            Assert.True(result.IsFullRun);
        }
        finally
        {
            try { Directory.Delete(looseDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void GraphNotInitialized_ReturnsFullRun()
    {
        var analyzer = new ImpactAnalyzer(_factory);
        // No InitializeAsync called

        var result = analyzer.Analyze([Change(_libCode)]);

        Assert.True(result.IsFullRun);
    }

    [Fact]
    public async Task AnalyzeFullRun_AlwaysReturnsFullRun()
    {
        var analyzer = new ImpactAnalyzer(_factory);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        var result = analyzer.AnalyzeFullRun();

        Assert.True(result.IsFullRun);
    }

    [Fact]
    public async Task InvalidateGraph_CausesNextAnalyzeToReturnFullRun()
    {
        var analyzer = new ImpactAnalyzer(_factory);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        analyzer.InvalidateGraph();

        var result = analyzer.Analyze([Change(_libCode)]);
        Assert.True(result.IsFullRun);
    }

    [Fact]
    public async Task SlnxChange_ReturnsFullRun()
    {
        var analyzer = new ImpactAnalyzer(_factory);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        var slnxPath = Path.Combine(_root, "MySolution.slnx");
        File.WriteAllText(slnxPath, "");

        var result = analyzer.Analyze([Change(slnxPath)]);

        Assert.True(result.IsFullRun);
    }
}
