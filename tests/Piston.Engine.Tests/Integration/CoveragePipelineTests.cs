// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Impact;
using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.Integration;

/// <summary>
/// End-to-end integration test for the coverage pipeline.
/// Verifies that:
/// (1) CoverageProcessor parses a Cobertura XML and persists coverage in SqliteCoverageStore,
/// (2) After storing coverage, HasCoverageData returns true for covered files,
/// (3) ImpactAnalyzer.Analyze() with the populated store produces AffectedTestFqns (Tier 3),
/// (4) When no coverage exists for a file, Tier 3 falls back to null (Tier 2 behavior).
/// </summary>
public sealed class CoveragePipelineTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private SqliteCoverageStore _store = null!;

    // Absolute path that will be the "covered file" in the coverage data
    private string _coveredFilePath = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _tempDir = Directory.CreateTempSubdirectory("piston-coverage-pipeline-").FullName;
        _coveredFilePath = Path.Combine(_tempDir, "src", "Lib", "Code.cs");

        // Create the source file on disk so CoberturaParser can resolve it via File.Exists
        _ = Directory.CreateDirectory(Path.GetDirectoryName(_coveredFilePath)!);
        File.WriteAllText(_coveredFilePath, "namespace Lib; public class Code {}");

        _store = new SqliteCoverageStore();
        await _store.InitializeAsync(_tempDir);
    }

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a Cobertura XML file that references a file at <paramref name="absoluteFilePath"/>.
    /// The source root is the parent directory of the file's grandparent (so the relative path
    /// within the XML resolves to the absolute path).
    /// </summary>
    private string WriteCoberturaXml(string absoluteFilePath, params int[] hitLines)
    {
        // The Cobertura XML uses a <source> element as the base directory.
        // filePath in the XML is relative to that source root.
        // We normalise: sourceRoot = Directory two levels above the file.
        var sourceRoot = Path.GetDirectoryName(Path.GetDirectoryName(absoluteFilePath))
            ?? Path.GetDirectoryName(absoluteFilePath)!;

        var relativePath = Path.GetRelativePath(sourceRoot, absoluteFilePath)
            .Replace('\\', '/');

        var lineElements = string.Join(
            Environment.NewLine,
            hitLines.Select(l => $"        <line number=\"{l}\" hits=\"1\" />"));

        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <coverage version="1.9" timestamp="1700000000">
              <sources><source>{sourceRoot.Replace('\\', '/')}</source></sources>
              <packages>
                <package name="Lib">
                  <classes>
                    <class filename="{relativePath}">
                      <lines>
            {lineElements}
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        var xmlPath = Path.Combine(_tempDir, $"{Guid.NewGuid():N}-coverage.cobertura.xml");
        File.WriteAllText(xmlPath, xml);
        return xmlPath;
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessCoverage_PopulatesStore_HasCoverageDataReturnsTrue()
    {
        var processor = new CoverageProcessor(new CoberturaParser());
        var testFqns = new[] { "Lib.Tests.LibTests.Passes" };
        var xmlPath = WriteCoberturaXml(_coveredFilePath, 5, 6, 7);
        var runId = _store.CreateRunId();

        _ = await processor.ProcessCoverageAsync(runId, [xmlPath], testFqns, _store);

        Assert.True(_store.HasCoverageData(_coveredFilePath));
    }

    [Fact]
    public async Task ProcessCoverage_GetTestsCoveringFile_ReturnsStoredTestFqns()
    {
        var processor = new CoverageProcessor(new CoberturaParser());
        var testFqns = new[] { "Lib.Tests.LibTests.Passes", "Lib.Tests.LibTests.AlsoPasses" };
        var xmlPath = WriteCoberturaXml(_coveredFilePath, 10, 11);
        var runId = _store.CreateRunId();

        _ = await processor.ProcessCoverageAsync(runId, [xmlPath], testFqns, _store);

        var tests = _store.GetTestsCoveringFile(_coveredFilePath);

        Assert.Equal(2, tests.Count);
        Assert.Contains("Lib.Tests.LibTests.Passes", tests);
        Assert.Contains("Lib.Tests.LibTests.AlsoPasses", tests);
    }

    [Fact]
    public async Task ProcessCoverage_DatabaseFileExists_InPistonSubdirectory()
    {
        // The store should have created .piston/piston.db under _tempDir
        var dbPath = Path.Combine(_tempDir, ".piston", "piston.db");

        // Even before processing, the DB file must exist after InitializeAsync
        Assert.True(File.Exists(dbPath));

        // Do a round-trip to confirm the file is a valid SQLite database
        var processor = new CoverageProcessor(new CoberturaParser());
        var xmlPath = WriteCoberturaXml(_coveredFilePath, 1);
        var runId = _store.CreateRunId();
        _ = await processor.ProcessCoverageAsync(runId, [xmlPath], ["Lib.Tests.T1"], _store);

        Assert.True(_store.HasCoverageData(_coveredFilePath));
    }

    [Fact]
    public async Task ImpactAnalyzer_WithCoverageStore_ProducesTier3FqnsForCoveredFile()
    {
        var source = "namespace Lib;\npublic class Code\n{\n    public int Value() => 1;\n}\n";
        File.WriteAllText(_coveredFilePath, source);
        var processor = new CoverageProcessor(new CoberturaParser());
        var testFqns = new[] { "Lib.Tests.LibTests.Passes" };
        var testProject = Path.Combine(_tempDir, "tests", "Lib.Tests.csproj");
        var xmlPath = WriteCoberturaXml(_coveredFilePath, 4);
        var runId = _store.CreateRunId();
        var processing = await processor.ProcessCoverageAsync(runId, [xmlPath], testFqns, _store,
            sourceRoot: _tempDir,
            impact: new CoverageImpactOptions(
                [new CoverageRunUnit(testProject, "run", [xmlPath], testFqns, Merge: false)],
                ImpactGranularity.Run,
                DateTime.UtcNow.AddSeconds(1)));
        Assert.Null(processing.Reports[xmlPath].Error);

        // Create a .csproj next to the covered file so Tier 1 can find the owning project
        var libDir = Path.GetDirectoryName(_coveredFilePath)!;
        var libCsproj = Path.Combine(libDir, "Lib.csproj");
        File.WriteAllText(libCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        File.WriteAllText(_coveredFilePath, source.Replace("=> 1", "=> 2", StringComparison.Ordinal));

        // Use a stub solution graph so we don't need MSBuild loaded
        var analyzer = new ImpactAnalyzer(
            _ => new StubSolutionGraph(libCsproj, isTestProject: false, testProject),
            _store, hasCompleteProjectCoverage: project => project == testProject);

        await analyzer.InitializeAsync("fake.slnx", CancellationToken.None);

        var changes = new[]
        {
            new FileChangeEvent(_coveredFilePath, WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)
        };

        var result = analyzer.Analyze(changes);

        Assert.NotNull(result.AffectedTestFqns);
        Assert.Equal([testProject], result.AffectedTestProjectPaths);
        Assert.Contains("Lib.Tests.LibTests.Passes", result.AffectedTestFqns!);
    }

    [Fact]
    public async Task ImpactAnalyzer_WithNoCoverageForFile_FallsBackToTier2()
    {
        // No coverage stored — store is empty
        // Create a .csproj next to the covered file so Tier 1 can find the owning project
        var libDir = Path.GetDirectoryName(_coveredFilePath)!;
        var libCsproj = Path.Combine(libDir, "Lib.csproj");
        File.WriteAllText(libCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var analyzer = new ImpactAnalyzer(
            _ => new StubSolutionGraph(libCsproj, isTestProject: false),
            _store);

        await analyzer.InitializeAsync("fake.slnx", CancellationToken.None);

        var changes = new[]
        {
            new FileChangeEvent(_coveredFilePath, WatcherChangeTypes.Changed, DateTimeOffset.UtcNow)
        };

        var result = analyzer.Analyze(changes);

        // No coverage data → Tier 3 must not activate
        Assert.Null(result.AffectedTestFqns);
    }

    [Fact]
    public async Task MarkFileStale_AfterProcessing_ExcludesFromTier3()
    {
        var processor = new CoverageProcessor(new CoberturaParser());
        var xmlPath = WriteCoberturaXml(_coveredFilePath, 5);
        var runId = _store.CreateRunId();
        _ = await processor.ProcessCoverageAsync(runId, [xmlPath], ["Lib.Tests.T1"], _store);

        // Confirm data exists
        Assert.True(_store.HasCoverageData(_coveredFilePath));

        // Mark stale (simulates a file change)
        await _store.MarkFileStaleAsync(_coveredFilePath);

        // After marking stale, HasCoverageData returns false and queries return empty
        Assert.False(_store.HasCoverageData(_coveredFilePath));
        Assert.Empty(_store.GetTestsCoveringFile(_coveredFilePath));
    }

    [Fact]
    public async Task MultipleRuns_SecondRunReplacesFirstRun_OnlyLatestDataVisible()
    {
        var processor = new CoverageProcessor(new CoberturaParser());
        const string testFqn = "Lib.Tests.LibTests.Passes";

        // Run 1: lines 1, 2, 3
        var xml1 = WriteCoberturaXml(_coveredFilePath, 1, 2, 3);
        var run1 = _store.CreateRunId();
        _ = await processor.ProcessCoverageAsync(run1, [xml1], [testFqn], _store);

        // Verify run 1 data
        Assert.True(_store.HasCoverageData(_coveredFilePath));
        Assert.NotEmpty(_store.GetTestsCoveringLines(_coveredFilePath, 1, 3));

        // Run 2: line 10 only (run1's lines should be gone)
        var xml2 = WriteCoberturaXml(_coveredFilePath, 10);
        var run2 = _store.CreateRunId();
        _ = await processor.ProcessCoverageAsync(run2, [xml2], [testFqn], _store);

        Assert.Empty(_store.GetTestsCoveringLines(_coveredFilePath, 1, 3));
        Assert.NotEmpty(_store.GetTestsCoveringLines(_coveredFilePath, 10, 10));
    }
}

/// <summary>
/// Minimal stub of <see cref="ISolutionGraph"/> that treats a single project as the owner
/// of any file query. Used to avoid MSBuild in the coverage pipeline integration tests.
/// </summary>
file sealed class StubSolutionGraph(string projectPath, bool isTestProject, string? dependentTestProject = null) : ISolutionGraph
{
    private readonly string _projectPath = Path.GetFullPath(projectPath);
    private readonly string? _dependentTestProject = dependentTestProject is null ? null : Path.GetFullPath(dependentTestProject);

    public IReadOnlyList<string> AllProjectPaths =>
        _dependentTestProject is { } project ? [_projectPath, project] : [_projectPath];
    public IReadOnlyList<string> TestProjectPaths =>
        _dependentTestProject is { } project ? [project] : isTestProject ? [_projectPath] : [];

    public string? FindOwningProject(string filePath) => _projectPath;

    public bool IsTestProject(string projectPath) =>
        (isTestProject && string.Equals(projectPath, _projectPath, StringComparison.Ordinal))
        || string.Equals(projectPath, _dependentTestProject, StringComparison.Ordinal);

    public IReadOnlySet<string> GetTransitiveDependents(string projectPath) =>
        new HashSet<string>(
            _dependentTestProject is { } project && string.Equals(projectPath, _projectPath, StringComparison.Ordinal)
                ? [project] : [], StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetSourceFiles(string projectPath) => [];
    public bool IsMtpProject(string projectPath) => false;
    public string? GetMtpOutputPath(string projectPath) => null;
}
