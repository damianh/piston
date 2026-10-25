// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.Data.Sqlite;
using Piston.Engine.Coverage;
using Piston.Engine.Impact;
using Xunit;

namespace Piston.Engine.Tests.Coverage;

/// <summary>
/// Tests for <see cref="SqliteCoverageStore"/> using a temp-file SQLite database.
/// </summary>
public sealed class SqliteCoverageStoreTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private string _tempDir = string.Empty;
    private string _dbDir = string.Empty;
    private SqliteCoverageStore _sut = null!;

    public async ValueTask InitializeAsync()
    {
        _tempDir = Directory.CreateTempSubdirectory("piston-coverage-store-test-").FullName;
        _dbDir = _tempDir; // InitializeAsync creates .piston/ inside this dir
        _sut = new SqliteCoverageStore();
        await _sut.InitializeAsync(_dbDir);
    }

    public ValueTask DisposeAsync()
    {
        _sut.Dispose();
        Directory.Delete(_tempDir, recursive: true);
        return ValueTask.CompletedTask;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, IReadOnlyList<TestLineCoverage>> MakeMap(
        string testFqn, string filePath, params int[] lines) =>
        new Dictionary<string, IReadOnlyList<TestLineCoverage>>
        {
            [testFqn] = lines.Select(l => new TestLineCoverage(filePath, l, 1)).ToList()
        };

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_RemovesFreshAndStaleRowsAndSummary_PersistsAndAllowsNewCoverage()
    {
        const string freshFile = "/repo/src/Fresh.cs";
        const string staleFile = "/repo/src/Stale.cs";
        var run = _sut.CreateRunId();
        await _sut.StoreCoverageAsync(run, MakeMap("Tests.Fresh", freshFile, 1));
        await _sut.StoreCoverageAsync(run, MakeMap("Tests.Stale", staleFile, 2));
        await _sut.MarkFileStaleAsync(staleFile);

        SqliteCoverageStore.Clear(_dbDir);
        SqliteCoverageStore.Clear(_dbDir);

        Assert.False(_sut.HasCoverageData(freshFile));
        Assert.Empty(_sut.GetTestsCoveringFile(freshFile));
        Assert.Empty(_sut.GetTestsCoveringLines(staleFile, 1, 10));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dbDir, ".piston", "piston.db"),
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync(_ct);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT COUNT(*) FROM coverage_set_lines) + (SELECT COUNT(*) FROM coverage_set_tests) + (SELECT COUNT(*) FROM coverage_sets) + (SELECT COUNT(*) FROM coverage_summary);";
            Assert.Equal(0L, await command.ExecuteScalarAsync(_ct));
        }

        _sut.Dispose();
        _sut = new SqliteCoverageStore();
        await _sut.InitializeAsync(_dbDir);
        Assert.False(_sut.HasCoverageData(freshFile));
        await _sut.StoreCoverageAsync(_sut.CreateRunId(), MakeMap("Tests.New", freshFile, 5));
        Assert.Equal(["Tests.New"], _sut.GetTestsCoveringFile(freshFile));
    }

    [Fact]
    public async Task Dispose_AfterClear_ReleasesDatabaseFileHandles()
    {
        await _sut.StoreCoverageAsync(_sut.CreateRunId(), MakeMap("Tests.Pass", "/repo/Code.cs", 1));
        SqliteCoverageStore.Clear(_dbDir);
        _sut.Dispose();

        var dbPath = Path.Combine(_dbDir, ".piston", "piston.db");
        using (var file = File.Open(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(file.Length > 0);
        }

        Directory.Delete(Path.Combine(_dbDir, ".piston"), recursive: true);
        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public async Task Clear_DoesNotAffectAnotherSolutionDirectory()
    {
        var otherDir = Path.Combine(_tempDir, "OtherSolution");
        using var other = new SqliteCoverageStore();
        await other.InitializeAsync(otherDir);
        const string file = "/repo/src/Shared.cs";
        await other.StoreCoverageAsync(other.CreateRunId(), MakeMap("Tests.Other", file, 1));
        await _sut.StoreCoverageAsync(_sut.CreateRunId(), MakeMap("Tests.Current", file, 1));

        SqliteCoverageStore.Clear(_dbDir);

        Assert.False(_sut.HasCoverageData(file));
        Assert.Equal(["Tests.Other"], other.GetTestsCoveringFile(file));
    }

    [Fact]
    public void InitializeAsync_CreatesDatabaseFile()
    {
        var dbPath = Path.Combine(_dbDir, ".piston", "piston.db");
        Assert.True(File.Exists(dbPath));
    }

    [Fact]
    public void CreateRunId_ReturnsIncreasingIds()
    {
        var id1 = _sut.CreateRunId();
        var id2 = _sut.CreateRunId();
        var id3 = _sut.CreateRunId();

        Assert.True(id1 < id2);
        Assert.True(id2 < id3);
    }

    [Fact]
    public async Task StoreCoverage_ThenGetTestsCoveringFile_ReturnsMatchingTests()
    {
        var runId = _sut.CreateRunId();
        var filePath = "/repo/src/Lib/Code.cs";
        var map = MakeMap("Lib.Tests.LibTests.Method1", filePath, 5, 6, 7);

        await _sut.StoreCoverageAsync(runId, map);

        var tests = _sut.GetTestsCoveringFile(filePath);

        _ = Assert.Single(tests);
        Assert.Equal("Lib.Tests.LibTests.Method1", tests[0]);
    }

    [Fact]
    public async Task StoreCoverage_ThenGetTestsCoveringLines_ReturnsMatchingTests()
    {
        var runId = _sut.CreateRunId();
        var filePath = "/repo/src/Lib/Code.cs";
        var map = MakeMap("Lib.Tests.LibTests.Method1", filePath, 5, 6, 7);

        await _sut.StoreCoverageAsync(runId, map);

        var tests = _sut.GetTestsCoveringLines(filePath, 5, 6);

        _ = Assert.Single(tests);
        Assert.Equal("Lib.Tests.LibTests.Method1", tests[0]);
    }

    [Fact]
    public async Task GetTestsCoveringLines_OutOfRange_ReturnsEmpty()
    {
        var runId = _sut.CreateRunId();
        var filePath = "/repo/src/Lib/Code.cs";
        var map = MakeMap("Lib.Tests.LibTests.Method1", filePath, 5, 6, 7);

        await _sut.StoreCoverageAsync(runId, map);

        var tests = _sut.GetTestsCoveringLines(filePath, 100, 200);

        Assert.Empty(tests);
    }

    [Fact]
    public async Task HasCoverageData_WhenDataExists_ReturnsTrue()
    {
        var runId = _sut.CreateRunId();
        var filePath = "/repo/src/Lib/Code.cs";
        var map = MakeMap("Lib.Tests.LibTests.Method1", filePath, 5);

        await _sut.StoreCoverageAsync(runId, map);

        Assert.True(_sut.HasCoverageData(filePath));
    }

    [Fact]
    public void HasCoverageData_WhenNoCoverageExists_ReturnsFalse() => Assert.False(_sut.HasCoverageData("/repo/src/NoSuchFile.cs"));

    [Fact]
    public async Task MarkFileStale_ExcludesFromQueryResults()
    {
        var runId = _sut.CreateRunId();
        var filePath = "/repo/src/Lib/Code.cs";
        var map = MakeMap("Lib.Tests.LibTests.Method1", filePath, 5);

        await _sut.StoreCoverageAsync(runId, map);
        await _sut.MarkFileStaleAsync(filePath);

        var tests = _sut.GetTestsCoveringFile(filePath);
        var hasData = _sut.HasCoverageData(filePath);

        Assert.Empty(tests);
        Assert.False(hasData);
    }

    [Fact]
    public async Task StoreCoverage_AfterStale_RestoresFreshData()
    {
        var filePath = "/repo/src/Lib/Code.cs";
        const string testFqn = "Lib.Tests.LibTests.Method1";

        // First run
        var run1 = _sut.CreateRunId();
        await _sut.StoreCoverageAsync(run1, MakeMap(testFqn, filePath, 5));

        // Mark stale (simulates file change)
        await _sut.MarkFileStaleAsync(filePath);
        Assert.Empty(_sut.GetTestsCoveringFile(filePath));

        // Second run — should restore fresh data
        var run2 = _sut.CreateRunId();
        await _sut.StoreCoverageAsync(run2, MakeMap(testFqn, filePath, 5, 6));

        var tests = _sut.GetTestsCoveringFile(filePath);
        _ = Assert.Single(tests);
        Assert.Equal(testFqn, tests[0]);
    }

    [Fact]
    public async Task StoreCoverage_MultipleTests_ReturnsAllDistinct()
    {
        var runId = _sut.CreateRunId();
        var filePath = "/repo/src/Lib/Code.cs";

        var map = new Dictionary<string, IReadOnlyList<TestLineCoverage>>
        {
            ["Lib.Tests.LibTests.Test1"] = [new TestLineCoverage(filePath, 5, 1)],
            ["Lib.Tests.LibTests.Test2"] = [new TestLineCoverage(filePath, 5, 1)],
            ["Lib.Tests.LibTests.Test3"] = [new TestLineCoverage(filePath, 6, 1)],
        };

        await _sut.StoreCoverageAsync(runId, map);

        var tests = _sut.GetTestsCoveringFile(filePath);

        Assert.Equal(3, tests.Count);
        Assert.Contains("Lib.Tests.LibTests.Test1", tests);
        Assert.Contains("Lib.Tests.LibTests.Test2", tests);
        Assert.Contains("Lib.Tests.LibTests.Test3", tests);
    }

    [Fact]
    public async Task StoreCoverage_DifferentFiles_QueriesIsolated()
    {
        var runId = _sut.CreateRunId();
        var file1 = "/repo/src/Lib/File1.cs";
        var file2 = "/repo/src/Lib/File2.cs";

        var map = new Dictionary<string, IReadOnlyList<TestLineCoverage>>
        {
            ["Lib.Tests.Test1"] = [new TestLineCoverage(file1, 1, 1)],
            ["Lib.Tests.Test2"] = [new TestLineCoverage(file2, 1, 1)],
        };

        await _sut.StoreCoverageAsync(runId, map);

        var file1Tests = _sut.GetTestsCoveringFile(file1);
        var file2Tests = _sut.GetTestsCoveringFile(file2);

        _ = Assert.Single(file1Tests);
        Assert.Equal("Lib.Tests.Test1", file1Tests[0]);

        _ = Assert.Single(file2Tests);
        Assert.Equal("Lib.Tests.Test2", file2Tests[0]);
    }

    [Fact]
    public void EmptyStore_GetTestsCoveringFile_ReturnsEmpty()
    {
        var tests = _sut.GetTestsCoveringFile("/some/file.cs");
        Assert.Empty(tests);
    }

    [Fact]
    public void EmptyStore_GetTestsCoveringLines_ReturnsEmpty()
    {
        var tests = _sut.GetTestsCoveringLines("/some/file.cs", 1, 10);
        Assert.Empty(tests);
    }

    [Fact]
    public async Task StoreCoverage_ReplacesOldDataForSameFiles()
    {
        var filePath = "/repo/src/Lib/Code.cs";
        const string testFqn = "Lib.Tests.Test1";

        // Run 1: lines 5 and 6
        var run1 = _sut.CreateRunId();
        await _sut.StoreCoverageAsync(run1, MakeMap(testFqn, filePath, 5, 6));

        // Run 2: only line 10 (lines 5 and 6 should be gone)
        var run2 = _sut.CreateRunId();
        await _sut.StoreCoverageAsync(run2, MakeMap(testFqn, filePath, 10));

        var linesRange = _sut.GetTestsCoveringLines(filePath, 5, 6);
        var line10Tests = _sut.GetTestsCoveringLines(filePath, 10, 10);

        Assert.Empty(linesRange);
        _ = Assert.Single(line10Tests);
    }

    [Fact]
    public async Task RunIdPersists_ReloadedStoreSeesCorrectCounter()
    {
        // Store some data with run 1
        var run1 = _sut.CreateRunId();
        var filePath = "/repo/src/Lib/Code.cs";
        await _sut.StoreCoverageAsync(run1, MakeMap("Lib.Tests.Test1", filePath, 5));

        // Dispose and re-open the same database
        _sut.Dispose();

        var sut2 = new SqliteCoverageStore();
        try
        {
            await sut2.InitializeAsync(_dbDir);
            var run2 = sut2.CreateRunId();

            // run2 must be > run1
            Assert.True(run2 > run1);
        }
        finally
        {
            sut2.Dispose();
        }

        // Re-init _sut for DisposeAsync
        _sut = new SqliteCoverageStore();
        await _sut.InitializeAsync(_dbDir);
    }

    private static CoverageReportFile ReportFile(string path, string? hash, params LineCoverage[] lines) =>
        new(new FileCoverage(path, lines, "Lib"), hash);

    private CoverageReportFile ReportFileWithContent(string path, string content, params LineCoverage[] lines) =>
        new(new FileCoverage(path, lines, "Lib"), ImpactContent.Hash(content), content);

    [Fact]
    public async Task StoreReportAsync_FullRun_ReplacesWholeReportWithTotals()
    {
        await _sut.StoreReportAsync(_sut.CreateRunId(), [ReportFile("/repo/Old.cs", "h0", new LineCoverage(1, 1))], isFullRun: true);
        var run = _sut.CreateRunId();

        await _sut.StoreReportAsync(run, [
            ReportFile("/repo/A.cs", "h1", new LineCoverage(1, 3, 1, 2), new LineCoverage(2, 0), new LineCoverage(5, 1)),
        ], isFullRun: true);

        var file = Assert.Single(_sut.GetReportFiles());
        Assert.Equal("/repo/A.cs", file.FilePath);
        Assert.Equal("Lib", file.Module);
        Assert.Equal((3, 2, 2, 1), (file.LinesTotal, file.LinesCovered, file.BranchesTotal, file.BranchesCovered));
        Assert.False(file.IsStale);
        Assert.False(file.IsPartial);
        Assert.Equal("h1", file.ContentHash);
        Assert.Equal(run, file.RunId);
        Assert.Equal(new LineCoverage(1, 3, 1, 2), _sut.GetReportLines("/repo/A.cs")[0]);
        Assert.Null(_sut.GetReportFile("/repo/Old.cs"));
    }

    [Fact]
    public async Task GetReportLines_InsertingCommentsAboveMethodRemapsCoverageAndKeepsItCurrent()
    {
        const string original = "class C\n{\n    void A()\n    {\n        int x = 1;\n    }\n}";
        const string updated = "class C\n{\n    // comment\n\n    void A()\n    {\n        int x = 1;\n    }\n}";
        var path = Path.Combine(_tempDir, "Code.cs");
        File.WriteAllText(path, original);
        await _sut.StoreReportAsync(_sut.CreateRunId(), [ReportFileWithContent(path, original, new LineCoverage(5, 1))], isFullRun: true);

        File.WriteAllText(path, updated);

        Assert.Equal([new LineCoverage(7, 1)], _sut.GetReportLines(path));
        Assert.Empty(_sut.GetReportStaleLines(path));
        Assert.False(Assert.Single(_sut.GetReportFiles()).IsStale);
    }

    [Fact]
    public async Task GetReportLines_MethodBodyEditDropsOldHitsAndMarksTheCurrentMethodStale()
    {
        const string original = "class C\n{\n    void A()\n    {\n        int x = 1;\n    }\n}";
        const string updated = "class C\n{\n    void A()\n    {\n        int x = 2;\n    }\n}";
        var path = Path.Combine(_tempDir, "Code.cs");
        File.WriteAllText(path, original);
        await _sut.StoreReportAsync(_sut.CreateRunId(), [ReportFileWithContent(path, original, new LineCoverage(5, 1))], isFullRun: true);

        File.WriteAllText(path, updated);

        Assert.Empty(_sut.GetReportLines(path));
        Assert.Equal([new LineSpan(3, 6)], _sut.GetReportStaleLines(path));
        Assert.True(Assert.Single(_sut.GetReportFiles()).IsStale);
    }

    [Fact]
    public async Task StoreReportAsync_SelectiveRunOnUnchangedFile_MergesHigherCounts()
    {
        await _sut.StoreReportAsync(_sut.CreateRunId(), [
            ReportFile("/repo/A.cs", "h1", new LineCoverage(1, 5), new LineCoverage(2, 0, 0, 2)),
            ReportFile("/repo/B.cs", "h2", new LineCoverage(1, 1)),
        ], isFullRun: true);

        await _sut.StoreReportAsync(_sut.CreateRunId(), [
            ReportFile("/repo/A.cs", "h1", new LineCoverage(1, 1), new LineCoverage(2, 2, 1, 2)),
        ], isFullRun: false);

        Assert.Equal([new LineCoverage(1, 5), new LineCoverage(2, 2, 1, 2)], _sut.GetReportLines("/repo/A.cs"));
        var a = _sut.GetReportFile("/repo/A.cs")!;
        Assert.Equal((2, 2, 1), (a.LinesCovered, a.BranchesTotal, a.BranchesCovered));
        Assert.False(a.IsPartial);
        Assert.NotNull(_sut.GetReportFile("/repo/B.cs"));
    }

    [Fact]
    public async Task StoreReportAsync_SelectiveRunOnChangedFile_ReplacesAndFlagsPartial()
    {
        await _sut.StoreReportAsync(_sut.CreateRunId(), [
            ReportFile("/repo/A.cs", "h1", new LineCoverage(1, 5), new LineCoverage(9, 1)),
        ], isFullRun: true);
        await _sut.MarkFileStaleAsync("/repo/A.cs");
        Assert.True(_sut.GetReportFile("/repo/A.cs")!.IsStale);

        await _sut.StoreReportAsync(_sut.CreateRunId(), [
            ReportFile("/repo/A.cs", "h2", new LineCoverage(1, 1)),
        ], isFullRun: false);

        Assert.Equal([new LineCoverage(1, 1)], _sut.GetReportLines("/repo/A.cs"));
        var a = _sut.GetReportFile("/repo/A.cs")!;
        Assert.False(a.IsStale);
        Assert.True(a.IsPartial);
        Assert.Equal("h2", a.ContentHash);
    }

    [Fact]
    public async Task Clear_RemovesReport()
    {
        await _sut.StoreReportAsync(_sut.CreateRunId(), [ReportFile("/repo/A.cs", "h1", new LineCoverage(1, 1))], isFullRun: true);

        SqliteCoverageStore.Clear(_dbDir);

        Assert.Empty(_sut.GetReportFiles());
        Assert.Empty(_sut.GetReportLines("/repo/A.cs"));
    }

    [Fact]
    public async Task InitializeAsync_DatabaseFromOlderVersion_DropsPerTestMapAndKeepsRunCounter()
    {
        _sut.Dispose();
        var dbPath = Path.Combine(_dbDir, ".piston", "piston.db");
        File.Delete(dbPath);
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await connection.OpenAsync(_ct);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE coverage_map (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INTEGER NOT NULL, test_fqn TEXT NOT NULL,
                    file_path TEXT NOT NULL, line_number INTEGER NOT NULL,
                    hit_count INTEGER NOT NULL DEFAULT 0, is_stale INTEGER NOT NULL DEFAULT 0);
                INSERT INTO coverage_map (run_id, test_fqn, file_path, line_number) VALUES (7, 'T', '/repo/A.cs', 1);
                """;
            _ = await command.ExecuteNonQueryAsync(_ct);
        }

        _sut = new SqliteCoverageStore();
        await _sut.InitializeAsync(_dbDir);

        Assert.Equal(8, _sut.CreateRunId());
        Assert.Empty(_sut.GetReportFiles());
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await connection.OpenAsync(_ct);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE name = 'coverage_map';";
            Assert.Equal(0L, await command.ExecuteScalarAsync(_ct));
        }
    }

    [Fact]
    public async Task Clear_DatabaseFromOlderVersion_InitializesCurrentSchema()
    {
        _sut.Dispose();
        var dbPath = Path.Combine(_dbDir, ".piston", "piston.db");
        File.Delete(dbPath);
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await connection.OpenAsync(_ct);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE coverage_map (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INTEGER NOT NULL, test_fqn TEXT NOT NULL,
                    file_path TEXT NOT NULL, line_number INTEGER NOT NULL,
                    hit_count INTEGER NOT NULL DEFAULT 0, is_stale INTEGER NOT NULL DEFAULT 0);
                INSERT INTO coverage_map (run_id, test_fqn, file_path, line_number) VALUES (7, 'T', '/repo/A.cs', 1);
                """;
            _ = await command.ExecuteNonQueryAsync(_ct);
        }

        SqliteCoverageStore.Clear(_dbDir);
        _sut = new SqliteCoverageStore();
        await _sut.InitializeAsync(_dbDir);

        Assert.Equal(1, _sut.CreateRunId());
        Assert.Empty(_sut.GetReportFiles());
    }

    [Fact]
    public async Task StoreCoverageAsync_TestsSharingLines_StoreTheLinesOnce()
    {
        var file = Path.Combine(_dbDir, "A.cs");
        IReadOnlyList<TestLineCoverage> lines = [.. Enumerable.Range(1, 50).Select(n => new TestLineCoverage(file, n, 1))];
        var map = Enumerable.Range(0, 100).ToDictionary(i => $"Tests.T{i}", _ => lines);

        await _sut.StoreCoverageAsync(_sut.CreateRunId(), map);

        Assert.Equal(100, _sut.GetTestsCoveringLines(file, 10, 10).Count);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_dbDir, ".piston", "piston.db")};Pooling=False");
        await connection.OpenAsync(_ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM coverage_set_lines;";
        Assert.Equal(50L, await command.ExecuteScalarAsync(_ct));
    }

    [Fact]
    public async Task StoreCoverageAsync_RemeasuredFile_ReplacesEarlierTestsAndPrunesEmptySets()
    {
        var a = Path.Combine(_dbDir, "A.cs");
        var b = Path.Combine(_dbDir, "B.cs");
        await _sut.StoreCoverageAsync(_sut.CreateRunId(), MakeMap("Tests.Old", a, 1));
        await _sut.StoreCoverageAsync(_sut.CreateRunId(), MakeMap("Tests.Other", b, 1));

        await _sut.StoreCoverageAsync(_sut.CreateRunId(), MakeMap("Tests.New", a, 2));

        Assert.Equal(["Tests.New"], _sut.GetTestsCoveringFile(a));
        Assert.Equal(["Tests.Other"], _sut.GetTestsCoveringFile(b));
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_dbDir, ".piston", "piston.db")};Pooling=False");
        await connection.OpenAsync(_ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM coverage_sets;";
        Assert.Equal(2L, await command.ExecuteScalarAsync(_ct));
    }
}
