using Microsoft.Data.Sqlite;
using Piston.Engine.Coverage;
using Xunit;

namespace Piston.Engine.Tests.Coverage;

/// <summary>
/// Tests for <see cref="SqliteCoverageStore"/> using a temp-file SQLite database.
/// </summary>
public sealed class SqliteCoverageStoreTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _dbDir = string.Empty;
    private SqliteCoverageStore _sut = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Directory.CreateTempSubdirectory("piston-coverage-store-test-").FullName;
        _dbDir   = _tempDir; // InitializeAsync creates .piston/ inside this dir
        _sut     = new SqliteCoverageStore();
        await _sut.InitializeAsync(_dbDir);
    }

    public Task DisposeAsync()
    {
        _sut.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        return Task.CompletedTask;
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

        Assert.Single(tests);
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

        Assert.Single(tests);
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
    public void HasCoverageData_WhenNoCoverageExists_ReturnsFalse()
    {
        Assert.False(_sut.HasCoverageData("/repo/src/NoSuchFile.cs"));
    }

    [Fact]
    public async Task MarkFileStale_ExcludesFromQueryResults()
    {
        var runId = _sut.CreateRunId();
        var filePath = "/repo/src/Lib/Code.cs";
        var map = MakeMap("Lib.Tests.LibTests.Method1", filePath, 5);

        await _sut.StoreCoverageAsync(runId, map);
        await _sut.MarkFileStaleAsync(filePath);

        var tests  = _sut.GetTestsCoveringFile(filePath);
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
        Assert.Single(tests);
        Assert.Equal(testFqn, tests[0]);
    }

    [Fact]
    public async Task StoreCoverage_MultipleTests_ReturnsAllDistinct()
    {
        var runId    = _sut.CreateRunId();
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
        var runId  = _sut.CreateRunId();
        var file1  = "/repo/src/Lib/File1.cs";
        var file2  = "/repo/src/Lib/File2.cs";

        var map = new Dictionary<string, IReadOnlyList<TestLineCoverage>>
        {
            ["Lib.Tests.Test1"] = [new TestLineCoverage(file1, 1, 1)],
            ["Lib.Tests.Test2"] = [new TestLineCoverage(file2, 1, 1)],
        };

        await _sut.StoreCoverageAsync(runId, map);

        var file1Tests = _sut.GetTestsCoveringFile(file1);
        var file2Tests = _sut.GetTestsCoveringFile(file2);

        Assert.Single(file1Tests);
        Assert.Equal("Lib.Tests.Test1", file1Tests[0]);

        Assert.Single(file2Tests);
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
        Assert.Single(line10Tests);
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
}
