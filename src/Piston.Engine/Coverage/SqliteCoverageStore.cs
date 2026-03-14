using Microsoft.Data.Sqlite;

namespace Piston.Engine.Coverage;

/// <summary>
/// SQLite-backed implementation of <see cref="ICoverageStore"/>.
/// Uses WAL journal mode for concurrent read performance.
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
///   is_stale    INTEGER NOT NULL DEFAULT 0
/// )
///
/// coverage_summary (
///   file_path      TEXT    PRIMARY KEY,
///   last_run_id    INTEGER NOT NULL,
///   last_updated   TEXT    NOT NULL
/// )
/// </code>
/// </remarks>
internal sealed class SqliteCoverageStore : ICoverageStore
{
    private SqliteConnection? _connection;
    private long _runCounter;

    public async Task InitializeAsync(string solutionDirectory)
    {
        var pistonDir = Path.Combine(solutionDirectory, ".piston");
        Directory.CreateDirectory(pistonDir);

        var dbPath = Path.Combine(pistonDir, "piston.db");
        _connection = new SqliteConnection($"Data Source={dbPath}");
        await _connection.OpenAsync().ConfigureAwait(false);

        // Enable WAL mode for concurrent read performance
        using var walCmd = _connection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        await walCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

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
            """;
        await createCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        // Seed the run counter from the current max run_id
        using var maxRunCmd = _connection.CreateCommand();
        maxRunCmd.CommandText = "SELECT COALESCE(MAX(run_id), 0) FROM coverage_map;";
        var result = await maxRunCmd.ExecuteScalarAsync().ConfigureAwait(false);
        _runCounter = result is long l ? l : Convert.ToInt64(result ?? 0L);
    }

    public long CreateRunId() => Interlocked.Increment(ref _runCounter);

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

            var pRunId      = insertCmd.CreateParameter(); pRunId.ParameterName = "$run_id";
            var pTestFqn    = insertCmd.CreateParameter(); pTestFqn.ParameterName = "$test_fqn";
            var pFilePath   = insertCmd.CreateParameter(); pFilePath.ParameterName = "$file_path";
            var pLineNumber = insertCmd.CreateParameter(); pLineNumber.ParameterName = "$line_number";
            var pHitCount   = insertCmd.CreateParameter(); pHitCount.ParameterName = "$hit_count";

            insertCmd.Parameters.Add(pRunId);
            insertCmd.Parameters.Add(pTestFqn);
            insertCmd.Parameters.Add(pFilePath);
            insertCmd.Parameters.Add(pLineNumber);
            insertCmd.Parameters.Add(pHitCount);

            // Delete old entries for these files before inserting fresh data
            var affectedFiles = testCoverageMap.Values
                .SelectMany(lines => lines.Select(l => l.FilePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            using var deleteCmd = _connection.CreateCommand();
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM coverage_map WHERE file_path = $file_path;";
            var pDeleteFile = deleteCmd.CreateParameter(); pDeleteFile.ParameterName = "$file_path";
            deleteCmd.Parameters.Add(pDeleteFile);

            foreach (var filePath in affectedFiles)
            {
                pDeleteFile.Value = filePath;
                await deleteCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // Insert new entries
            pRunId.Value = runId;
            foreach (var (testFqn, lines) in testCoverageMap)
            {
                pTestFqn.Value = testFqn;
                foreach (var line in lines)
                {
                    pFilePath.Value   = line.FilePath;
                    pLineNumber.Value = line.LineNumber;
                    pHitCount.Value   = line.HitCount;
                    await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            // Update coverage_summary
            using var summaryCmd = _connection.CreateCommand();
            summaryCmd.Transaction = transaction;
            summaryCmd.CommandText = """
                INSERT OR REPLACE INTO coverage_summary (file_path, last_run_id, last_updated)
                VALUES ($file_path, $run_id, $now);
                """;
            var pSummaryFile   = summaryCmd.CreateParameter(); pSummaryFile.ParameterName = "$file_path";
            var pSummaryRunId  = summaryCmd.CreateParameter(); pSummaryRunId.ParameterName = "$run_id";
            var pSummaryNow    = summaryCmd.CreateParameter(); pSummaryNow.ParameterName = "$now";

            summaryCmd.Parameters.Add(pSummaryFile);
            summaryCmd.Parameters.Add(pSummaryRunId);
            summaryCmd.Parameters.Add(pSummaryNow);

            pSummaryRunId.Value = runId;
            pSummaryNow.Value   = DateTimeOffset.UtcNow.ToString("O");

            foreach (var filePath in affectedFiles)
            {
                pSummaryFile.Value = filePath;
                await summaryCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
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
        cmd.Parameters.AddWithValue("$file_path", filePath);

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));

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
        cmd.Parameters.AddWithValue("$file_path", filePath);
        cmd.Parameters.AddWithValue("$start", startLine);
        cmd.Parameters.AddWithValue("$end", endLine);

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));

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
        cmd.Parameters.AddWithValue("$file_path", filePath);
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
            """;
        cmd.Parameters.AddWithValue("$file_path", filePath);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    private void EnsureInitialized()
    {
        if (_connection is null)
            throw new InvalidOperationException(
                $"{nameof(SqliteCoverageStore)} has not been initialized. Call {nameof(InitializeAsync)} first.");
    }
}
