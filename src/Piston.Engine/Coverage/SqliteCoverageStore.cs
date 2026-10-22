// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.Data.Sqlite;

namespace Piston.Engine.Coverage;

/// <summary>
/// SQLite-backed implementation of <see cref="ICoverageStore"/>.
/// Uses WAL journal mode for concurrent read performance.
/// Connections are not pooled so disposal releases the database's file handles.
/// </summary>
/// <remarks>
/// Schema:
/// <code>
/// coverage_map (
///   id          INTEGER PRIMARY KEY,
///   run_id      INTEGER NOT NULL,
///   test_fqn    TEXT    NOT NULL,
///   file_path   TEXT    NOT NULL,
///   line_number INTEGER NOT NULL,
///   hit_count   INTEGER NOT NULL DEFAULT 0,
///   is_stale    INTEGER NOT NULL DEFAULT 0,
///   granularity TEXT    NOT NULL DEFAULT 'run'  -- 'run': every test in the run is credited with every line
/// )
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
internal sealed class SqliteCoverageStore : ICoverageStore
{
    private const string ReportSchema = """
        CREATE TABLE IF NOT EXISTS coverage_files (
            file_path        TEXT    PRIMARY KEY,
            module           TEXT,
            content_hash     TEXT,
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

    private SqliteConnection? _connection;
    private long _runCounter;

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

        // Create tables
        using var createCmd = _connection.CreateCommand();
        createCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS coverage_map (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id      INTEGER NOT NULL,
                test_fqn    TEXT    NOT NULL,
                file_path   TEXT    NOT NULL,
                line_number INTEGER NOT NULL,
                hit_count   INTEGER NOT NULL DEFAULT 0,
                is_stale    INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS coverage_summary (
                file_path    TEXT    PRIMARY KEY,
                last_run_id  INTEGER NOT NULL,
                last_updated TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_coverage_map_file
                ON coverage_map (file_path, is_stale);

            CREATE INDEX IF NOT EXISTS idx_coverage_map_file_line
                ON coverage_map (file_path, line_number, is_stale);

            CREATE INDEX IF NOT EXISTS idx_coverage_map_run
                ON coverage_map (run_id);
            """ + ReportSchema;
        _ = await createCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        // Databases created before attribution granularity was recorded lack the column.
        using var columnsCmd = _connection.CreateCommand();
        columnsCmd.CommandText = "SELECT COUNT(1) FROM pragma_table_info('coverage_map') WHERE name = 'granularity';";
        if (await columnsCmd.ExecuteScalarAsync().ConfigureAwait(false) is 0L)
        {
            using var alterCmd = _connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE coverage_map ADD COLUMN granularity TEXT NOT NULL DEFAULT 'run';";
            _ = await alterCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // Seed the run counter from the current max run_id
        using var maxRunCmd = _connection.CreateCommand();
        maxRunCmd.CommandText = """
            SELECT MAX(
                (SELECT COALESCE(MAX(run_id), 0) FROM coverage_map),
                (SELECT COALESCE(MAX(run_id), 0) FROM coverage_files));
            """;
        var result = await maxRunCmd.ExecuteScalarAsync().ConfigureAwait(false);
        _runCounter = result is long l ? l : Convert.ToInt64(result ?? 0L);
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
        command.CommandText = ReportSchema + """
            DELETE FROM coverage_map;
            DELETE FROM coverage_summary;
            DELETE FROM coverage_files;
            DELETE FROM coverage_lines;
            """;
        _ = command.ExecuteNonQuery();
        transaction.Commit();
    }

    public async Task StoreCoverageAsync(
        long runId,
        IReadOnlyDictionary<string, IReadOnlyList<TestLineCoverage>> testCoverageMap)
    {
        EnsureInitialized();

        using var transaction = _connection!.BeginTransaction();
        try
        {
            using var insertCmd = _connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = """
                INSERT OR REPLACE INTO coverage_map
                    (run_id, test_fqn, file_path, line_number, hit_count, is_stale)
                VALUES
                    ($run_id, $test_fqn, $file_path, $line_number, $hit_count, 0);
                """;

            var pRunId = insertCmd.CreateParameter(); pRunId.ParameterName = "$run_id";
            var pTestFqn = insertCmd.CreateParameter(); pTestFqn.ParameterName = "$test_fqn";
            var pFilePath = insertCmd.CreateParameter(); pFilePath.ParameterName = "$file_path";
            var pLineNumber = insertCmd.CreateParameter(); pLineNumber.ParameterName = "$line_number";
            var pHitCount = insertCmd.CreateParameter(); pHitCount.ParameterName = "$hit_count";

            _ = insertCmd.Parameters.Add(pRunId);
            _ = insertCmd.Parameters.Add(pTestFqn);
            _ = insertCmd.Parameters.Add(pFilePath);
            _ = insertCmd.Parameters.Add(pLineNumber);
            _ = insertCmd.Parameters.Add(pHitCount);

            // Delete old entries for these files before inserting fresh data
            var affectedFiles = testCoverageMap.Values
                .SelectMany(lines => lines.Select(l => l.FilePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            using var deleteCmd = _connection.CreateCommand();
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM coverage_map WHERE file_path = $file_path;";
            var pDeleteFile = deleteCmd.CreateParameter(); pDeleteFile.ParameterName = "$file_path";
            _ = deleteCmd.Parameters.Add(pDeleteFile);

            foreach (var filePath in affectedFiles)
            {
                pDeleteFile.Value = filePath;
                _ = await deleteCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // Insert new entries
            pRunId.Value = runId;
            foreach (var (testFqn, lines) in testCoverageMap)
            {
                pTestFqn.Value = testFqn;
                foreach (var line in lines)
                {
                    pFilePath.Value = line.FilePath;
                    pLineNumber.Value = line.LineNumber;
                    pHitCount.Value = line.HitCount;
                    _ = await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            // Update coverage_summary
            using var summaryCmd = _connection.CreateCommand();
            summaryCmd.Transaction = transaction;
            summaryCmd.CommandText = """
                INSERT OR REPLACE INTO coverage_summary (file_path, last_run_id, last_updated)
                VALUES ($file_path, $run_id, $now);
                """;
            var pSummaryFile = summaryCmd.CreateParameter(); pSummaryFile.ParameterName = "$file_path";
            var pSummaryRunId = summaryCmd.CreateParameter(); pSummaryRunId.ParameterName = "$run_id";
            var pSummaryNow = summaryCmd.CreateParameter(); pSummaryNow.ParameterName = "$now";

            _ = summaryCmd.Parameters.Add(pSummaryFile);
            _ = summaryCmd.Parameters.Add(pSummaryRunId);
            _ = summaryCmd.Parameters.Add(pSummaryNow);

            pSummaryRunId.Value = runId;
            pSummaryNow.Value = DateTimeOffset.UtcNow.ToString("O");

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
        EnsureInitialized();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT test_fqn
            FROM coverage_map
            WHERE file_path = $file_path
              AND is_stale = 0;
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
        EnsureInitialized();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT test_fqn
            FROM coverage_map
            WHERE file_path = $file_path
              AND line_number BETWEEN $start AND $end
              AND is_stale = 0;
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
        EnsureInitialized();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1)
            FROM coverage_map
            WHERE file_path = $file_path
              AND is_stale = 0
            LIMIT 1;
            """;
        _ = cmd.Parameters.AddWithValue("$file_path", filePath);
        var result = cmd.ExecuteScalar();
        return result is long count && count > 0;
    }

    public async Task MarkFileStaleAsync(string filePath)
    {
        EnsureInitialized();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            UPDATE coverage_map
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
        EnsureInitialized();

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

                if (!isFullRun && ReadFile(path, transaction) is { } existing
                    && existing.ContentHash is not null
                    && string.Equals(existing.ContentHash, file.ContentHash, StringComparison.Ordinal))
                {
                    // Same source as last measured: a subset run can only add hits, never remove them.
                    lines = MergeLines(ReadLines(path, transaction), lines);
                    isPartial = existing.IsPartial;
                }

                await WriteFileAsync(runId, file, lines, isPartial, now, transaction).ConfigureAwait(false);
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
        EnsureInitialized();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"{SelectFileColumns} ORDER BY file_path;";
        var results = new List<CoverageFileSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadFileSummary(reader));
        }

        return results;
    }

    public CoverageFileSummary? GetReportFile(string filePath)
    {
        EnsureInitialized();
        return ReadFile(filePath, transaction: null);
    }

    public IReadOnlyList<LineCoverage> GetReportLines(string filePath)
    {
        EnsureInitialized();
        return ReadLines(filePath, transaction: null);
    }

    private const string SelectFileColumns = """
        SELECT file_path, module, lines_total, lines_covered, branches_total, branches_covered,
               is_stale, is_partial, content_hash, run_id, updated_at
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
        UpdatedAt: DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture));

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
                (file_path, module, content_hash, lines_total, lines_covered, branches_total, branches_covered,
                 is_stale, is_partial, run_id, updated_at)
            VALUES
                ($file_path, $module, $hash, $lt, $lc, $bt, $bc, 0, $partial, $run_id, $now);
            """;
        _ = fileCmd.Parameters.AddWithValue("$file_path", path);
        _ = fileCmd.Parameters.AddWithValue("$module", (object?)file.Coverage.Module ?? DBNull.Value);
        _ = fileCmd.Parameters.AddWithValue("$hash", (object?)file.ContentHash ?? DBNull.Value);
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
        _connection?.Dispose();
        _connection = null;
    }

    private void EnsureInitialized()
    {
        if (_connection is null)
        {
            throw new InvalidOperationException(
                $"{nameof(SqliteCoverageStore)} has not been initialized. Call {nameof(InitializeAsync)} first.");
        }
    }
}
