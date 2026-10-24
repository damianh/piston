// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Piston.Engine.Tests.Coverage;
using Xunit;

namespace Piston.Engine.Tests.Impact;

public sealed class ImpactAnalyzerTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

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
            transitiveDependencies: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [_appCsproj] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _libCsproj },
                [_testsCsproj] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _appCsproj, _libCsproj },
            },
            fileToProject: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [_libCode] = _libCsproj,
                [_appProgram] = _appCsproj,
                [_testsFile] = _testsCsproj,
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
    public async Task CsprojChange_RefreshesGraphAndIncludesProjectAndDependents()
    {
        var analyzer = new ImpactAnalyzer(_factory);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        var result = analyzer.Analyze([Change(_libCsproj)]);

        Assert.False(result.RequiresGraphRebuild);
        Assert.Contains(_libCsproj, result.AffectedProjectPaths, StringComparer.OrdinalIgnoreCase);

        var sourceResult = analyzer.Analyze([Change(_libCode)]);
        Assert.False(sourceResult.IsFullRun);
        Assert.Contains(_libCsproj, sourceResult.AffectedProjectPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PropsChange_RefreshesGraphWithoutRequestingRedundantRebuild()
    {
        var analyzer = new ImpactAnalyzer(_factory);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        var propsPath = Path.Combine(_root, "Directory.Build.props");
        File.WriteAllText(propsPath, "<Project />");
        var result = analyzer.Analyze([Change(propsPath)]);

        Assert.True(result.IsFullRun);
        Assert.False(result.RequiresGraphRebuild);
        Assert.False(analyzer.Analyze([Change(_libCode)]).IsFullRun);
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
    public async Task FullRunAfterInvalidation_RebuildsGraphBeforeEnumeratingTestProjects()
    {
        ISolutionGraph graph = _graph;
        var analyzer = new ImpactAnalyzer(_ => graph);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);
        analyzer.InvalidateGraph();
        graph = new StubSolutionGraph([_appCsproj, _testsCsproj], [_appCsproj, _testsCsproj]);

        Assert.True(analyzer.AnalyzeFullRun().IsFullRun);
        Assert.Equal([_appCsproj, _testsCsproj], analyzer.GetAllTestProjectPaths());
    }

    [Fact]
    public async Task FailedFullRunRefresh_DropsStaleGraphAndFallsBackToSolutionTarget()
    {
        var fail = false;
        var analyzer = new ImpactAnalyzer(_ => fail ? throw new InvalidOperationException("bad graph") : _graph);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);
        fail = true;

        Assert.True(analyzer.AnalyzeFullRun().IsFullRun);
        Assert.Empty(analyzer.GetAllTestProjectPaths());
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

    // ── Tier 3 tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Tier3_WhenCoverageDataExistsForAllChangedFiles_ResultContainsAffectedTestFqns()
    {
        var coverageStore = new StubCoverageStore();
        var libCodeFull = Path.GetFullPath(_libCode);

        // Pre-configure coverage store to indicate libCode is covered by Test1
        _ = coverageStore.FilesWithCoverage.Add(libCodeFull);
        coverageStore.TestsCoveringFile[libCodeFull] = ["Lib.Tests.LibTests.Test1"];

        var analyzer = new ImpactAnalyzer(_factory, coverageStore);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        var result = analyzer.Analyze([Change(_libCode)]);

        Assert.False(result.IsFullRun);
        Assert.NotNull(result.AffectedTestFqns);
        Assert.Contains("Lib.Tests.LibTests.Test1", result.AffectedTestFqns!);
    }

    [Fact]
    public async Task Tier3_WhenOneCoverageFileMissing_FallsBackToNullFqns()
    {
        var coverageStore = new StubCoverageStore();
        var libCodeFull = Path.GetFullPath(_libCode);

        // Only libCode has coverage; appProgram does NOT
        _ = coverageStore.FilesWithCoverage.Add(libCodeFull);
        coverageStore.TestsCoveringFile[libCodeFull] = ["Lib.Tests.Test1"];
        // _appProgram is NOT in FilesWithCoverage

        var analyzer = new ImpactAnalyzer(_factory, coverageStore);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        // Change both files — one lacks coverage
        var result = analyzer.Analyze([Change(_libCode), Change(_appProgram)]);

        Assert.False(result.IsFullRun);
        // Falls back to Tier 2: AffectedTestFqns should be null
        Assert.Null(result.AffectedTestFqns);
    }

    [Fact]
    public async Task Tier3_WhenCoverageStoreIsNull_Tier2BehaviorUnchanged()
    {
        // No coverage store — should behave exactly like pre-coverage Tier 2
        var analyzer = new ImpactAnalyzer(_factory, coverageStore: null);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        var result = analyzer.Analyze([Change(_libCode)]);

        Assert.False(result.IsFullRun);
        Assert.Null(result.AffectedTestFqns);
        Assert.Contains(_testsCsproj, result.AffectedTestProjectPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tier3_StaleMarkingCalledForChangedCsFiles()
    {
        var coverageStore = new StubCoverageStore();
        var libCodeFull = Path.GetFullPath(_libCode);

        _ = coverageStore.FilesWithCoverage.Add(libCodeFull);
        coverageStore.TestsCoveringFile[libCodeFull] = ["Lib.Tests.Test1"];

        var analyzer = new ImpactAnalyzer(_factory, coverageStore);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        _ = analyzer.Analyze([Change(_libCode)]);

        // Allow any pending async MarkFileStaleAsync to complete
        await Task.Delay(50, _ct);

        Assert.Contains(libCodeFull, coverageStore.MarkedStaleFiles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tier3_WhenCoverageReturnsEmptyFqns_FallsBackToTier2()
    {
        var coverageStore = new StubCoverageStore();
        var libCodeFull = Path.GetFullPath(_libCode);

        // File has coverage data but no tests listed
        _ = coverageStore.FilesWithCoverage.Add(libCodeFull);
        coverageStore.TestsCoveringFile[libCodeFull] = [];

        var analyzer = new ImpactAnalyzer(_factory, coverageStore);
        await analyzer.InitializeAsync("solution.slnx", CancellationToken.None);

        var result = analyzer.Analyze([Change(_libCode)]);

        Assert.False(result.IsFullRun);
        // Empty FQN set → falls back to Tier 2 (null)
        Assert.Null(result.AffectedTestFqns);
    }

    // ── Tier 3 with the impact map ─────────────────────────────────────────────

    private static readonly string LibSource = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"int line{i} = {i};"));

    private async Task<SqliteCoverageStore> ImpactStoreAsync()
    {
        File.WriteAllText(_libCode, LibSource);
        var store = new SqliteCoverageStore();
        await store.InitializeAsync(_root);
        var lib = Path.GetFullPath(_libCode);
        await store.RecordImpactAsync(new ImpactRecording(
            _testsCsproj, ImpactGranularity.Class, "T.A", store.CreateRunId(), ["T.A.One"], [new ImpactFile(lib, [new(2, 4)], LibSource)], Merge: false));
        await store.RecordImpactAsync(new ImpactRecording(
            _testsCsproj, ImpactGranularity.Class, "T.B", store.CreateRunId(), ["T.B.One"], [new ImpactFile(lib, [new(10, 12)], LibSource)], Merge: false));
        return store;
    }

    private void EditLibLine(int line)
    {
        var lines = SnapshotDiff.SplitLines(LibSource);
        lines[line - 1] = $"int changedLine{line} = {line};";
        File.WriteAllText(_libCode, string.Join("\n", lines));
    }

    [Fact]
    public async Task Tier3_ImpactMap_SelectsOnlyTestsWhoseExecutedLinesTheEditTouches()
    {
        using var store = await ImpactStoreAsync();
        var analyzer = new ImpactAnalyzer(_factory, store);
        await analyzer.InitializeAsync("solution.slnx", _ct);
        EditLibLine(11);

        var result = analyzer.Analyze([Change(_libCode)]);

        Assert.Equal(["T.B.One"], result.AffectedTestFqns);
        var reason = Assert.Single(result.SelectionReasons!["T.B.One"]);
        Assert.Equal(("11", "10-12", "class"), (reason.ChangedLines, reason.ExecutedLines, reason.Granularity));
        Assert.Equal(_testsCsproj, reason.ProjectPath);
    }

    [Fact]
    public async Task Tier3_ImpactMap_AnalyzeMarksClassUnitsDirty_PreviewDoesNot()
    {
        using var store = await ImpactStoreAsync();
        var analyzer = new ImpactAnalyzer(_factory, store);
        await analyzer.InitializeAsync("solution.slnx", _ct);
        EditLibLine(3);

        var preview = analyzer.Preview([Change(_libCode)]);

        Assert.Equal(["T.A.One"], preview.AffectedTestFqns);
        Assert.All(store.GetUnits(_testsCsproj, ImpactGranularity.Class), u => Assert.True(u.IsCurrent));

        _ = analyzer.Analyze([Change(_libCode)]);

        Assert.All(store.GetUnits(_testsCsproj, ImpactGranularity.Class), u => Assert.False(u.IsCurrent));
    }

    [Fact]
    public async Task Tier3_ImpactMap_CommentAndWhitespaceOnlyEditSkipsTestsAndKeepsUnitsCurrent()
    {
        using var store = await ImpactStoreAsync();
        var analyzer = new ImpactAnalyzer(_factory, store);
        await analyzer.InitializeAsync("solution.slnx", _ct);
        var edited = LibSource.Replace("int line11 = 11;", "int line11=11; // comment", StringComparison.Ordinal);
        File.WriteAllText(_libCode, "// heading\n" + edited);

        var result = analyzer.Analyze([Change(_libCode)]);

        Assert.Empty(result.AffectedTestProjectPaths);
        Assert.Empty(result.AffectedTestFqns!);
        Assert.Contains("Only comments or whitespace", result.Explanation, StringComparison.Ordinal);
        Assert.All(store.GetUnits(_testsCsproj, ImpactGranularity.Class), unit => Assert.True(unit.IsCurrent));
    }

    [Fact]
    public async Task Tier3_ImpactMap_CommentOnlyTestFileEditSkipsTestsWhenMapped()
    {
        using var store = await ImpactStoreAsync();
        var testSource = "// test file";
        await store.RecordImpactAsync(new ImpactRecording(
            _testsCsproj,
            ImpactGranularity.Test,
            "T.A.One",
            store.CreateRunId(),
            ["T.A.One"],
            [new ImpactFile(Path.GetFullPath(_testsFile), [new(1, 1)], testSource)],
            Merge: false));

        var analyzer = new ImpactAnalyzer(_factory, store);
        await analyzer.InitializeAsync("solution.slnx", _ct);
        File.WriteAllText(_testsFile, "// heading\n// updated comment");

        var result = analyzer.Analyze([Change(_testsFile)]);

        Assert.Empty(result.AffectedTestProjectPaths);
        Assert.Empty(result.AffectedTestFqns!);
        Assert.Contains("Only comments or whitespace", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tier3_ImpactMap_UnmappedFile_FallsBackToTier2()
    {
        using var store = await ImpactStoreAsync();
        var analyzer = new ImpactAnalyzer(_factory, store);
        await analyzer.InitializeAsync("solution.slnx", _ct);
        EditLibLine(3);

        var result = analyzer.Analyze([Change(_libCode), Change(_appProgram)]);

        Assert.Null(result.AffectedTestFqns);
        Assert.Contains(_testsCsproj, result.AffectedTestProjectPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Program.cs", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tier3_ImpactMap_TestFileChange_FallsBackToTier2()
    {
        using var store = await ImpactStoreAsync();
        var analyzer = new ImpactAnalyzer(_factory, store);
        await analyzer.InitializeAsync("solution.slnx", _ct);
        EditLibLine(3);

        var result = analyzer.Analyze([Change(_libCode), Change(_testsFile)]);

        Assert.Null(result.AffectedTestFqns);
        Assert.Contains(_testsCsproj, result.AffectedTestProjectPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("test file changed", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProjectPaths_ForFullBuild_AreAllProjects()
    {
        var analyzer = CreateInitialized();

        Assert.Equal(
            new[] { _libCsproj, _appCsproj, _testsCsproj }.Order(),
            analyzer.GetBuildProjectPaths(null).Order());
    }

    [Fact]
    public void BuildProjectPaths_ForTargets_IncludeTheirReferences()
    {
        var analyzer = CreateInitialized();

        Assert.Equal(
            new[] { _libCsproj, _appCsproj }.Order(),
            analyzer.GetBuildProjectPaths([_appCsproj]).Order());
    }

    [Fact]
    public void BuildProjectPaths_WithoutGraph_AreEmpty() =>
        Assert.Empty(new ImpactAnalyzer(_factory).GetBuildProjectPaths(null));
}
