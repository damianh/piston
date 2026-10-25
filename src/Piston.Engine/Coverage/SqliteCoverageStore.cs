// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.Data.Sqlite;
using Piston.Engine.Impact;

namespace Piston.Engine.Coverage;

/// <summary>
/// SQLite-backed implementation of <see cref="ICoverageStore"/>.
/// Uses WAL journal mode for concurrent read performance.
/// Connections are not pooled so disposal releases the database's file handles.
/// </summary>
/// <remarks>
/// Schema:
/// <code>
/// -- Which tests covered which lines. Tests that share the same covered lines share one set, so a run
/// -- credited as a whole stores its lines once rather than once per test.
/// coverage_sets      (set_id INTEGER PRIMARY KEY, run_id INTEGER, granularity TEXT)  -- 'run': every test in the run is credited with every line
/// coverage_set_tests (set_id, test_fqn, PRIMARY KEY (set_id, test_fqn))
/// coverage_set_lines (file_path, line_number, set_id, hit_count, is_stale, PRIMARY KEY (file_path, line_number, set_id))
///
/// coverage_summary (
///   file_path      TEXT    PRIMARY KEY,
///   last_run_id    INTEGER NOT NULL,
///   last_updated   TEXT    NOT NULL
/// )
///
/// -- Solution-wide line/branch report, independent of which tests covered what.
/// coverage_files (
///   file_path TEXT PRIMARY KEY, module TEXT, content_hash TEXT,
///   lines_total, lines_covered, branches_total, branches_covered INTEGER,
///   is_stale INTEGER, is_partial INTEGER, run_id INTEGER, updated_at TEXT
/// )
///
/// coverage_lines (
///   file_path TEXT, line_number INTEGER, hits INTEGER,
///   branches_covered INTEGER, branches_total INTEGER,
///   PRIMARY KEY (file_path, line_number)
/// )
/// </code>
/// </remarks>
internal sealed partial class SqliteCoverageStore : ICoverageStore
{
    private const string SetSchema = """
        CREATE TABLE IF NOT EXISTS coverage_sets (
            set_id      INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id      INTEGER NOT NULL,
            granularity TEXT    NOT NULL DEFAULT 'run'
        );

        CREATE TABLE IF NOT EXISTS coverage_set_tests (
            set_id   INTEGER NOT NULL,
            test_fqn TEXT    NOT NULL,
            PRIMARY KEY (set_id, test_fqn)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS coverage_set_lines (
            file_path   TEXT    NOT NULL,
            line_number INTEGER NOT NULL,
            set_id      INTEGER NOT NULL,
            hit_count   INTEGER NOT NULL DEFAULT 0,
            is_stale    INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (file_path, line_number, set_id)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS idx_coverage_set_lines_set
            ON coverage_set_lines (set_id);
        """;

    private const string ReportSchema = """
        CREATE TABLE IF NOT EXISTS coverage_files (
            file_path        TEXT    PRIMARY KEY,
            module           TEXT,
            content_hash     TEXT,
            snapshot_hash    TEXT,
            lines_total      INTEGER NOT NULL,
            lines_covered    INTEGER NOT NULL,
            branches_total   INTEGER NOT NULL,
            branches_covered INTEGER NOT NULL,
            is_stale         INTEGER NOT NULL DEFAULT 0,
            is_partial       INTEGER NOT NULL DEFAULT 0,
            run_id           INTEGER NOT NULL,
            updated_at       TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS coverage_lines (
            file_path        TEXT    NOT NULL,
            line_number      INTEGER NOT NULL,
            hits             INTEGER NOT NULL,
            branches_covered INTEGER NOT NULL DEFAULT 0,
            branches_total   INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (file_path, line_number)
        ) WITHOUT ROWID;
        """;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private long _runCounter;
    private const int SchemaVersion = 2;

    public async Task InitializeAsync(string solutionDirectory)
    {
        var pistonDir = Path.Combine(solutionDirectory, ".piston");
        _ = Directory.CreateDirectory(pistonDir);

        var dbPath = Path.Combine(pistonDir, "piston.db");
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false,
        }.ToString());
        await _connection.OpenAsync().ConfigureAwait(false);

        // Enable WAL mode for concurrent read performance
        using var walCmd = _connection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        _ = await walCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        long legacyMaxRunId = 0;
        using (var legacyCmd = _connection.CreateCommand())
        {
            legacyCmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'coverage_map';";
            if (await legacyCmd.ExecuteScalarAsync().ConfigureAwait(false) is 1L)
            {
                legacyCmd.CommandText = "SELECT COALESCE(MAX(run_id), 0) FROM coverage_map;";
                legacyMaxRunId = Convert.ToInt64(await legacyCmd.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
            }
        }

        using (var versionCmd = _connection.CreateCommand())
        {
            versionCmd.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(await versionCmd.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            versionCmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            var tableCount = Convert.ToInt32(await versionCmd.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (tableCount > 0 && version != SchemaVersion)
            {
                versionCmd.CommandText = """
                    DROP TABLE IF EXISTS coverage_map;
                    DROP TABLE IF EXISTS coverage_set_lines;
                    DROP TABLE IF EXISTS coverage_set_tests;
                    DROP TABLE IF EXISTS coverage_sets;
                    DROP TABLE IF EXISTS coverage_summary;
                    DROP TABLE IF EXISTS coverage_lines;
                    DROP TABLE IF EXISTS coverage_files;
                    DROP TABLE IF EXISTS impact_unit_files;
                    DROP TABLE IF EXISTS impact_unit_tests;
                    DROP TABLE IF EXISTS impact_units;
                    DROP TABLE IF EXISTS impact_snapshots;
                    """;
                _ = await versionCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

        }

        // Create tables
        using var createCmd = _connection.CreateCommand();
        createCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS coverage_summary (
                file_path    TEXT    PRIMARY KEY,
                last_run_id  INTEGER NOT NULL,
                last_updated TEXT    NOT NULL
            );

            """ + SetSchema + ReportSchema + ImpactSchema;
        _ = await createCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        using (var versionCmd = _connection.CreateCommand())
        {
            versionCmd.CommandText = $"PRAGMA user_version={SchemaVersion};";
            _ = await versionCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // Older databases stored one coverage_map row per test per line, which grows to millions of rows on
        // real solutions. It is only a cache, so drop it; the next run with coverage refills the sets.
        // Seed the run counter from the current max run_id
        using var maxRunCmd = _connection.CreateCommand();
        maxRunCmd.CommandText = """
            SELECT MAX(
                $legacy,
                (SELECT COALESCE(MAX(run_id), 0) FROM coverage_sets),
                (SELECT COALESCE(MAX(run_id), 0) FROM coverage_files),
                (SELECT COALESCE(MAX(run_id), 0) FROM impact_units));
            """;
        _ = maxRunCmd.Parameters.AddWithValue("$legacy", legacyMaxRunId);
        var result = await maxRunCmd.ExecuteScalarAsync().ConfigureAwait(false);
        _runCounter = result is long l ? l : Convert.ToInt64(result ?? 0L);

        // Reclaim the space a dropped legacy table or deleted rows left behind.
        using var checkpointCmd = _connection.CreateCommand();
        checkpointCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        _ = await checkpointCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public long CreateRunId() => Interlocked.Increment(ref _runCounter);

    public static void Clear(string solutionDirectory)
    {
        var dbPath = Path.Combine(solutionDirectory, ".piston", "piston.db");
        if (!File.Exists(dbPath))
        {
            return;
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        using (var versionCmd = connection.CreateCommand())
        {
            versionCmd.Transaction = transaction;
            versionCmd.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(versionCmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (version != SchemaVersion)
            {
                command.CommandText = """
                    DROP TABLE IF EXISTS coverage_map;
                    DROP TABLE IF EXISTS coverage_set_lines;
                    DROP TABLE IF EXISTS coverage_set_tests;
                    DROP TABLE IF EXISTS coverage_sets;
                    DROP TABLE IF EXISTS coverage_summary;
                    DROP TABLE IF EXISTS coverage_lines;
                    DROP TABLE IF EXISTS coverage_files;
                    DROP TABLE IF EXISTS impact_unit_files;
                    DROP TABLE IF EXISTS impact_unit_tests;
                    DROP TABLE IF EXISTS impact_units;
                    DROP TABLE IF EXISTS impact_snapshots;
                    """;
                _ = command.ExecuteNonQuery();
            }
        }

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS coverage_summary (
                file_path    TEXT    PRIMARY KEY,
                last_run_id  INTEGER NOT NULL,
                last_updated TEXT    NOT NULL
            );
            """ + SetSchema + ReportSchema + ImpactSchema + ClearImpact + """
            DROP TABLE IF EXISTS coverage_map;
            DELETE FROM coverage_set_lines;
            DELETE FROM coverage_set_tests;
            DELETE FROM coverage_sets;
            DELETE FROM coverage_summary;
            DELETE FROM coverage_files;
            DELETE FROM coverage_lines;
            PRAGMA user_version=2;
            """;
        _ = command.ExecuteNonQuery();
        transaction.Commit();
    }

    public async Task StoreCoverageAsync(
        long runId,
        IReadOnlyDictionary<string, IReadOnlyList<TestLineCoverage>> testCoverageMap)
    {
        using var gate = Enter();

        // Tests given the same line list (by reference) share one set.
        var sets = new Dictionary<IReadOnlyList<TestLineCoverage>, List<string>>(ReferenceEqualityComparer.Instance);
        foreach (var (testFqn, lines) in testCoverageMap)
        {
            if (!sets.TryGetValue(lines, out var tests))
            {
                sets[lines] = tests = [];
            }

            tests.Add(testFqn);
        }

        var affectedFiles = testCoverageMap.Values
            .Distinct(ReferenceEqualityComparer.Instance)
            .Cast<IReadOnlyList<TestLineCoverage>>()
            .SelectMany(lines => lines.Select(l => l.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var transaction = _connection!.BeginTransaction();
        try
        {
            // Re-measured files replace what earlier runs recorded for them.
            using (var deleteCmd = _connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = "DELETE FROM coverage_set_lines WHERE file_path = $file_path;";
                var pDeleteFile = deleteCmd.Parameters.Add("$file_path", SqliteType.Text);
                foreach (var filePath in affectedFiles)
                {
                    pDeleteFile.Value = filePath;
                    _ = await deleteCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            using var setCmd = _connection.CreateCommand();
            setCmd.Transaction = transaction;
            setCmd.CommandText = "INSERT INTO coverage_sets (run_id, granularity) VALUES ($run_id, 'run') RETURNING set_id;";
            _ = setCmd.Parameters.AddWithValue("$run_id", runId);

            using var testCmd = _connection.CreateCommand();
            testCmd.Transaction = transaction;
            testCmd.CommandText = "INSERT OR IGNORE INTO coverage_set_tests (set_id, test_fqn) VALUES ($set_id, $test_fqn);";
            var pTestSet = testCmd.Parameters.Add("$set_id", SqliteType.Integer);
            var pTestFqn = testCmd.Parameters.Add("$test_fqn", SqliteType.Text);

            using var lineCmd = _connection.CreateCommand();
            lineCmd.Transaction = transaction;
            lineCmd.CommandText = """
                INSERT OR REPLACE INTO coverage_set_lines (file_path, line_number, set_id, hit_count, is_stale)
                VALUES ($file_path, $line_number, $set_id, $hit_count, 0);
                """;
            var pFilePath = lineCmd.Parameters.Add("$file_path", SqliteType.Text);
            var pLineNumber = lineCmd.Parameters.Add("$line_number", SqliteType.Integer);
            var pLineSet = lineCmd.Parameters.Add("$set_id", SqliteType.Integer);
            var pHitCount = lineCmd.Parameters.Add("$hit_count", SqliteType.Integer);

            foreach (var (lines, tests) in sets)
            {
                if (lines.Count == 0)
                {
                    continue;
                }

                var setId = Convert.ToInt64(await setCmd.ExecuteScalarAsync().ConfigureAwait(false));
                pTestSet.Value = setId;
                foreach (var test in tests)
                {
                    pTestFqn.Value = test;
                    _ = await testCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                pLineSet.Value = setId;
                foreach (var line in lines)
                {
                    pFilePath.Value = line.FilePath;
                    pLineNumber.Value = line.LineNumber;
                    pHitCount.Value = line.HitCount;
                    _ = await lineCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            // Drop sets whose every line has been re-measured by later runs.
            using (var pruneCmd = _connection.CreateCommand())
            {
                pruneCmd.Transaction = transaction;
                pruneCmd.CommandText = """
                    DELETE FROM coverage_sets
                    WHERE NOT EXISTS (SELECT 1 FROM coverage_set_lines l WHERE l.set_id = coverage_sets.set_id);
                    DELETE FROM coverage_set_tests
                    WHERE NOT EXISTS (SELECT 1 FROM coverage_sets s WHERE s.set_id = coverage_set_tests.set_id);
                    """;
                _ = await pruneCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using var summaryCmd = _connection.CreateCommand();
            summaryCmd.Transaction = transaction;
            summaryCmd.CommandText = """
                INSERT OR REPLACE INTO coverage_summary (file_path, last_run_id, last_updated)
                VALUES ($file_path, $run_id, $now);
                """;
            var pSummaryFile = summaryCmd.Parameters.Add("$file_path", SqliteType.Text);
            _ = summaryCmd.Parameters.AddWithValue("$run_id", runId);
            _ = summaryCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            foreach (var filePath in affectedFiles)
            {
                pSummaryFile.Value = filePath;
                _ = await summaryCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            throw;
        }
    }

    public IReadOnlyList<string> GetTestsCoveringFile(string filePath)
    {
        using var gate = Enter();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT t.test_fqn
            FROM coverage_set_lines l
            JOIN coverage_set_tests t ON t.set_id = l.set_id
            WHERE l.file_path = $file_path
              AND l.is_stale = 0;
            """;
        _ = cmd.Parameters.AddWithValue("$file_path", filePath);

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    public IReadOnlyList<string> GetTestsCoveringLines(string filePath, int startLine, int endLine)
    {
        using var gate = Enter();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT t.test_fqn
            FROM coverage_set_lines l
            JOIN coverage_set_tests t ON t.set_id = l.set_id
            WHERE l.file_path = $file_path
              AND l.line_number BETWEEN $start AND $end
              AND l.is_stale = 0;
            """;
        _ = cmd.Parameters.AddWithValue("$file_path", filePath);
        _ = cmd.Parameters.AddWithValue("$start", startLine);
        _ = cmd.Parameters.AddWithValue("$end", endLine);

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    public bool HasCoverageData(string filePath)
    {
        using var gate = Enter();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM coverage_set_lines
                WHERE file_path = $file_path AND is_stale = 0);
            """;
        _ = cmd.Parameters.AddWithValue("$file_path", filePath);
        var result = cmd.ExecuteScalar();
        return result is long count && count > 0;
    }

    public async Task MarkFileStaleAsync(string filePath)
    {
        using var gate = Enter();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            UPDATE coverage_set_lines
            SET is_stale = 1
            WHERE file_path = $file_path;

            UPDATE coverage_files
            SET is_stale = 1
            WHERE file_path = $file_path;
            """;
        _ = cmd.Parameters.AddWithValue("$file_path", filePath);
        _ = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task StoreReportAsync(long runId, IReadOnlyList<CoverageReportFile> files, bool isFullRun)
    {
        using var gate = Enter();

        using var transaction = _connection!.BeginTransaction();
        try
        {
            if (isFullRun)
            {
                using var resetCmd = _connection.CreateCommand();
                resetCmd.Transaction = transaction;
                resetCmd.CommandText = "DELETE FROM coverage_lines; DELETE FROM coverage_files;";
                _ = await resetCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            foreach (var file in files)
            {
                var path = file.Coverage.FilePath;
                var lines = file.Coverage.Lines;
                var isPartial = !isFullRun;

                if (!isFullRun && ReadFile(path, transaction) is { } existing)
                {
                    if (existing.ContentHash is not null
                        && string.Equals(existing.ContentHash, file.ContentHash, StringComparison.Ordinal))
                    {
                        // Same source as last measured: a subset run can only add hits, never remove them.
                        lines = MergeLines(ReadLines(path, transaction), lines);
                        isPartial = existing.IsPartial;
                    }
                    else if (existing.SnapshotHash is not null
                        && file.Content is not null
                        && ReadSnapshot(existing.SnapshotHash, transaction) is { } oldContent)
                    {
                        var remapped = RemapLines(oldContent, file.Content, ReadLines(path, transaction));
                        lines = MergeLines(remapped, lines);
                    }
                }

                await WriteFileAsync(runId, file, lines, isPartial, now, transaction).ConfigureAwait(false);
            }

            using (var pruneCmd = _connection.CreateCommand())
            {
                pruneCmd.Transaction = transaction;
                pruneCmd.CommandText = """
                    DELETE FROM impact_snapshots
                    WHERE NOT EXISTS (SELECT 1 FROM impact_unit_files f WHERE f.snapshot_hash = impact_snapshots.snapshot_hash)
                      AND NOT EXISTS (SELECT 1 FROM coverage_files c WHERE c.snapshot_hash = impact_snapshots.snapshot_hash);
                    """;
                _ = await pruneCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            throw;
        }
    }

    public IReadOnlyList<CoverageFileSummary> GetReportFiles()
    {
        using var gate = Enter();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"{SelectFileColumns} ORDER BY file_path;";
        var results = new List<CoverageFileSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadFileSummary(reader));
        }

        return [.. results.Select(RefreshStaleness)];
    }

    public CoverageFileSummary? GetReportFile(string filePath)
    {
        using var gate = Enter();
        var file = ReadFile(filePath, transaction: null);
        return file is null ? null : RefreshStaleness(file);
    }

    public IReadOnlyList<LineCoverage> GetReportLines(string filePath)
    {
        using var gate = Enter();
        var summary = ReadFile(filePath, transaction: null);
        var currentContent = ImpactContent.TryRead(filePath);
        if (summary is null)
        {
            return [];
        }

        if (currentContent is null)
        {
            return [];
        }

        var currentHash = ImpactContent.Hash(currentContent);
        if (string.Equals(summary.SnapshotHash ?? summary.ContentHash, currentHash, StringComparison.Ordinal))
        {
            return ReadLines(filePath, transaction: null);
        }

        if (summary.SnapshotHash is not { } snapshotHash || ReadSnapshot(snapshotHash, null) is not { } oldContent)
        {
            return [];
        }

        return RemapLines(oldContent, currentContent, ReadLines(filePath, transaction: null));
    }

    public IReadOnlyList<LineSpan> GetReportStaleLines(string filePath)
    {
        using var gate = Enter();
        var currentContent = ImpactContent.TryRead(filePath);
        if (currentContent is null)
        {
            return [];
        }

        var summary = ReadFile(filePath, transaction: null);
        if (summary is null)
        {
            return [];
        }

        var currentHash = ImpactContent.Hash(currentContent);
        if (string.Equals(summary.SnapshotHash ?? summary.ContentHash, currentHash, StringComparison.Ordinal))
        {
            return [];
        }

        if (summary.SnapshotHash is not { } snapshotHash || ReadSnapshot(snapshotHash, null) is not { } oldContent)
        {
            return [new LineSpan(1, SnapshotDiff.SplitLines(currentContent).Length)];
        }

        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return [new LineSpan(1, SnapshotDiff.SplitLines(currentContent).Length)];
        }

        var oldMembers = SourceMembers.Parse(oldContent);
        var currentMembers = SourceMembers.Parse(currentContent);
        if (oldMembers.Members.Count == 0 || currentMembers.Members.Count == 0
            || oldMembers.ResidualHash != currentMembers.ResidualHash)
        {
            return [new LineSpan(1, SnapshotDiff.SplitLines(currentContent).Length)];
        }

        var oldByKey = oldMembers.Members.ToDictionary(member => member.Key, StringComparer.Ordinal);
        return LineSpan.Normalize(currentMembers.Members
            .Where(member => !oldByKey.TryGetValue(member.Key, out var oldMember)
                || oldMember.BodyHash != member.BodyHash)
            .Select(member => member.Lines));
    }

    private const string SelectFileColumns = """
        SELECT file_path, module, lines_total, lines_covered, branches_total, branches_covered,
               is_stale, is_partial, content_hash, run_id, updated_at
               , snapshot_hash
        FROM coverage_files
        """;

    private static CoverageFileSummary ReadFileSummary(SqliteDataReader reader) => new(
        FilePath: reader.GetString(0),
        Module: reader.IsDBNull(1) ? null : reader.GetString(1),
        LinesTotal: reader.GetInt32(2),
        LinesCovered: reader.GetInt32(3),
        BranchesTotal: reader.GetInt32(4),
        BranchesCovered: reader.GetInt32(5),
        IsStale: reader.GetInt64(6) != 0,
        IsPartial: reader.GetInt64(7) != 0,
        ContentHash: reader.IsDBNull(8) ? null : reader.GetString(8),
        RunId: reader.GetInt64(9),
        UpdatedAt: DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture),
        SnapshotHash: reader.IsDBNull(11) ? null : reader.GetString(11));

    private CoverageFileSummary RefreshStaleness(CoverageFileSummary file)
    {
        var currentContent = ImpactContent.TryRead(file.FilePath);
        if (currentContent is null)
        {
            return file with { IsStale = true };
        }

        var currentHash = ImpactContent.Hash(currentContent);
        if (string.Equals(file.SnapshotHash ?? file.ContentHash, currentHash, StringComparison.Ordinal))
        {
            return file with { IsStale = false };
        }

        var oldContent = file.SnapshotHash is null ? null : ReadSnapshot(file.SnapshotHash, null);
        var sameCode = oldContent is not null
            && file.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            && SourceMembers.Parse(oldContent).HasSameCode(SourceMembers.Parse(currentContent));
        return file with { IsStale = !sameCode };
    }

    private CoverageFileSummary? ReadFile(string filePath, SqliteTransaction? transaction)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"{SelectFileColumns} WHERE file_path = $file_path;";
        _ = cmd.Parameters.AddWithValue("$file_path", filePath);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFileSummary(reader) : null;
    }

    private List<LineCoverage> ReadLines(string filePath, SqliteTransaction? transaction)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT line_number, hits, branches_covered, branches_total
            FROM coverage_lines
            WHERE file_path = $file_path
            ORDER BY line_number;
            """;
        _ = cmd.Parameters.AddWithValue("$file_path", filePath);
        var results = new List<LineCoverage>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new LineCoverage(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
        }

        return results;
    }

    private static IReadOnlyList<LineCoverage> RemapLines(string oldContent, string currentContent, IReadOnlyList<LineCoverage> lines)
    {
        var oldMembers = SourceMembers.Parse(oldContent);
        var currentMembers = SourceMembers.Parse(currentContent);
        if (oldMembers.Members.Count == 0 || currentMembers.Members.Count == 0)
        {
            return [];
        }

        var lineMap = oldMembers.MapLinesTo(currentMembers);
        var result = new List<LineCoverage>();
        foreach (var line in lines)
        {
            if (!lineMap.TryGetValue(line.LineNumber, out var newLines))
            {
                continue;
            }

            result.AddRange(newLines.Select(lineNumber => line with { LineNumber = lineNumber }));
        }

        return MergeLines([], result);
    }

    private static List<LineCoverage> MergeLines(IReadOnlyList<LineCoverage> existing, IReadOnlyList<LineCoverage> incoming)
    {
        var merged = existing.ToDictionary(l => l.LineNumber);
        foreach (var line in incoming)
        {
            merged[line.LineNumber] = merged.TryGetValue(line.LineNumber, out var old)
                ? new LineCoverage(
                    line.LineNumber,
                    Math.Max(old.HitCount, line.HitCount),
                    Math.Max(old.BranchesCovered, line.BranchesCovered),
                    Math.Max(old.BranchesTotal, line.BranchesTotal))
                : line;
        }

        return [.. merged.Values.OrderBy(l => l.LineNumber)];
    }

    private async Task WriteFileAsync(
        long runId,
        CoverageReportFile file,
        IReadOnlyList<LineCoverage> lines,
        bool isPartial,
        string now,
        SqliteTransaction transaction)
    {
        var path = file.Coverage.FilePath;

        using (var deleteCmd = _connection!.CreateCommand())
        {
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM coverage_lines WHERE file_path = $file_path;";
            _ = deleteCmd.Parameters.AddWithValue("$file_path", path);
            _ = await deleteCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using (var lineCmd = _connection.CreateCommand())
        {
            lineCmd.Transaction = transaction;
            lineCmd.CommandText = """
                INSERT INTO coverage_lines (file_path, line_number, hits, branches_covered, branches_total)
                VALUES ($file_path, $line, $hits, $bc, $bt);
                """;
            _ = lineCmd.Parameters.AddWithValue("$file_path", path);
            var pLine = lineCmd.Parameters.Add("$line", SqliteType.Integer);
            var pHits = lineCmd.Parameters.Add("$hits", SqliteType.Integer);
            var pBranchesCovered = lineCmd.Parameters.Add("$bc", SqliteType.Integer);
            var pBranchesTotal = lineCmd.Parameters.Add("$bt", SqliteType.Integer);
            foreach (var line in lines)
            {
                pLine.Value = line.LineNumber;
                pHits.Value = line.HitCount;
                pBranchesCovered.Value = line.BranchesCovered;
                pBranchesTotal.Value = line.BranchesTotal;
                _ = await lineCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        using var fileCmd = _connection.CreateCommand();
        fileCmd.Transaction = transaction;
        fileCmd.CommandText = """
            INSERT OR REPLACE INTO coverage_files
                (file_path, module, content_hash, snapshot_hash, lines_total, lines_covered, branches_total, branches_covered,
                 is_stale, is_partial, run_id, updated_at)
            VALUES
                ($file_path, $module, $hash, $snapshot_hash, $lt, $lc, $bt, $bc, 0, $partial, $run_id, $now);
            """;
        if (file.Content is not null && file.ContentHash is not null)
        {
            using var snapshotCmd = _connection.CreateCommand();
            snapshotCmd.Transaction = transaction;
            snapshotCmd.CommandText = "INSERT OR IGNORE INTO impact_snapshots (snapshot_hash, content) VALUES ($hash, $content);";
            _ = snapshotCmd.Parameters.AddWithValue("$hash", file.ContentHash);
            _ = snapshotCmd.Parameters.AddWithValue("$content", file.Content);
            _ = await snapshotCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        _ = fileCmd.Parameters.AddWithValue("$file_path", path);
        _ = fileCmd.Parameters.AddWithValue("$module", (object?)file.Coverage.Module ?? DBNull.Value);
        _ = fileCmd.Parameters.AddWithValue("$hash", (object?)file.ContentHash ?? DBNull.Value);
        _ = fileCmd.Parameters.AddWithValue("$snapshot_hash", file.Content is null ? DBNull.Value : file.ContentHash ?? (object)DBNull.Value);
        _ = fileCmd.Parameters.AddWithValue("$lt", lines.Count);
        _ = fileCmd.Parameters.AddWithValue("$lc", lines.Count(l => l.HitCount > 0));
        _ = fileCmd.Parameters.AddWithValue("$bt", lines.Sum(l => l.BranchesTotal));
        _ = fileCmd.Parameters.AddWithValue("$bc", lines.Sum(l => l.BranchesCovered));
        _ = fileCmd.Parameters.AddWithValue("$partial", isPartial ? 1 : 0);
        _ = fileCmd.Parameters.AddWithValue("$run_id", runId);
        _ = fileCmd.Parameters.AddWithValue("$now", now);
        _ = await fileCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// Serializes use of the single connection: the orchestrator and the impact analyzer call the store from
    /// different threads, and a <see cref="SqliteConnection"/> must not be used concurrently.
    /// </summary>
    private Gate Enter()
    {
        _gate.Wait();
        if (_connection is null)
        {
            _ = _gate.Release();
            throw new InvalidOperationException(
                $"{nameof(SqliteCoverageStore)} has not been initialized. Call {nameof(InitializeAsync)} first.");
        }

        return new Gate(_gate);
    }

    private readonly struct Gate(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
