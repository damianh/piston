// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Piston.Engine.Impact;
using Piston.Engine.Services;

namespace Piston.Engine.Coverage;

/// <remarks>
/// Impact map schema:
/// <code>
/// impact_units      (unit_id, project_path, granularity, unit_key, run_id, is_dirty, recorded_at)
///                   -- a measured unit: a project's run (granularity 0), a class (1) or a test (2)
/// impact_unit_tests (unit_id, test_fqn)                      -- the tests credited with the unit's files
/// impact_unit_files (unit_id, file_path, snapshot_hash, spans, member_keys) -- executed member identities
/// impact_snapshots  (snapshot_hash, content)                  -- file content when measured, shared by hash
/// </code>
/// </remarks>
internal sealed partial class SqliteCoverageStore : IImpactMapStore
{
    private const string ImpactSchema = """
        CREATE TABLE IF NOT EXISTS impact_units (
            unit_id      INTEGER PRIMARY KEY AUTOINCREMENT,
            project_path TEXT    NOT NULL,
            granularity  INTEGER NOT NULL,
            unit_key     TEXT    NOT NULL,
            run_id       INTEGER NOT NULL,
            is_dirty     INTEGER NOT NULL DEFAULT 0,
            recorded_at  TEXT    NOT NULL,
            UNIQUE (project_path, granularity, unit_key)
        );

        CREATE TABLE IF NOT EXISTS impact_unit_tests (
            unit_id  INTEGER NOT NULL,
            test_fqn TEXT    NOT NULL,
            PRIMARY KEY (unit_id, test_fqn)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS impact_unit_files (
            unit_id       INTEGER NOT NULL,
            file_path     TEXT    NOT NULL,
            snapshot_hash TEXT    NOT NULL,
            spans         TEXT    NOT NULL,
            member_keys   TEXT    NOT NULL DEFAULT '[]',
            PRIMARY KEY (unit_id, file_path)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS idx_impact_unit_files_path
            ON impact_unit_files (file_path);

        CREATE INDEX IF NOT EXISTS idx_impact_unit_files_snapshot
            ON impact_unit_files (snapshot_hash);

        CREATE TABLE IF NOT EXISTS impact_snapshots (
            snapshot_hash TEXT PRIMARY KEY,
            content       TEXT NOT NULL
        ) WITHOUT ROWID;
        """;

    private const string ClearImpact = """
        DELETE FROM impact_unit_files;
        DELETE FROM impact_unit_tests;
        DELETE FROM impact_units;
        DELETE FROM impact_snapshots;
        """;

    public async Task RecordImpactAsync(ImpactRecording recording)
    {
        using var gate = Enter();

        using var transaction = _connection!.BeginTransaction();
        try
        {
            using var unitCmd = Command(transaction, """
                INSERT INTO impact_units (project_path, granularity, unit_key, run_id, is_dirty, recorded_at)
                VALUES ($project, $granularity, $key, $run_id, 0, $now)
                ON CONFLICT (project_path, granularity, unit_key) DO UPDATE SET
                    run_id = excluded.run_id,
                    recorded_at = excluded.recorded_at,
                    is_dirty = CASE WHEN $merge THEN impact_units.is_dirty ELSE 0 END
                RETURNING unit_id;
                """);
            _ = unitCmd.Parameters.AddWithValue("$project", recording.ProjectPath);
            _ = unitCmd.Parameters.AddWithValue("$granularity", (int)recording.Granularity);
            _ = unitCmd.Parameters.AddWithValue("$key", recording.UnitKey);
            _ = unitCmd.Parameters.AddWithValue("$run_id", recording.RunId);
            _ = unitCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            _ = unitCmd.Parameters.AddWithValue("$merge", recording.Merge);
            var unitId = Convert.ToInt64(await unitCmd.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);

            if (!recording.Merge)
            {
                using var resetCmd = Command(transaction, """
                    DELETE FROM impact_unit_tests WHERE unit_id = $unit_id;
                    DELETE FROM impact_unit_files WHERE unit_id = $unit_id;
                    """);
                _ = resetCmd.Parameters.AddWithValue("$unit_id", unitId);
                _ = await resetCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            if (recording.Merge && recording.MethodsComplete)
            {
                await PruneRenamedTestsAsync(unitId, recording.TestFqns, transaction).ConfigureAwait(false);
            }

            using (var testCmd = Command(transaction, "INSERT OR IGNORE INTO impact_unit_tests (unit_id, test_fqn) VALUES ($unit_id, $fqn);"))
            {
                _ = testCmd.Parameters.AddWithValue("$unit_id", unitId);
                var pFqn = testCmd.Parameters.Add("$fqn", SqliteType.Text);
                foreach (var fqn in recording.TestFqns.Distinct(StringComparer.Ordinal))
                {
                    pFqn.Value = fqn;
                    _ = await testCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            using var snapshotCmd = Command(transaction, "INSERT OR IGNORE INTO impact_snapshots (snapshot_hash, content) VALUES ($hash, $content);");
            var pSnapshotHash = snapshotCmd.Parameters.Add("$hash", SqliteType.Text);
            var pSnapshotContent = snapshotCmd.Parameters.Add("$content", SqliteType.Text);

            using var fileCmd = Command(transaction, """
                INSERT OR REPLACE INTO impact_unit_files (unit_id, file_path, snapshot_hash, spans, member_keys)
                VALUES ($unit_id, $path, $hash, $spans, $members);
                """);
            _ = fileCmd.Parameters.AddWithValue("$unit_id", unitId);
            var pPath = fileCmd.Parameters.Add("$path", SqliteType.Text);
            var pHash = fileCmd.Parameters.Add("$hash", SqliteType.Text);
            var pSpans = fileCmd.Parameters.Add("$spans", SqliteType.Text);
            var pMembers = fileCmd.Parameters.Add("$members", SqliteType.Text);

            foreach (var file in recording.Files)
            {
                var hash = ImpactContent.Hash(file.Content);
                var sourceMembers = SourceMembers.Parse(file.Content);
                var memberKeys = sourceMembers.Members.Count == 0
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : MembersForSpans(sourceMembers, file.Spans).ToHashSet(StringComparer.Ordinal);
                if (sourceMembers.Members.Count > 0 && HasLooseSpans(sourceMembers, file.Spans))
                {
                    _ = memberKeys.Add("*");
                }
                IEnumerable<LineSpan> spans = file.Spans;

                if (recording.Merge && ReadUnitFile(unitId, file.FilePath, transaction) is { } existing)
                {
                    var earlierKeys = ReadMemberKeys(unitId, file.FilePath, transaction);
                    var earlierSpans = LineSpan.Parse(existing.Spans);
                    if (ReadSnapshot(existing.Hash, transaction) is { } oldContent)
                    {
                        var oldMembers = SourceMembers.Parse(oldContent);
                        if (oldMembers.Members.Count == 0 || sourceMembers.Members.Count == 0)
                        {
                            var oldLines = LineSpan.Parse(existing.Spans);
                            var diff = SnapshotDiff.Compute(SnapshotDiff.SplitLines(oldContent), SnapshotDiff.SplitLines(file.Content));
                            spans = spans.Concat(diff is { } changed ? changed.Remap(oldLines) : oldLines);
                        }
                        else
                        {
                            var unchanged = oldMembers.Members
                                .Join(sourceMembers.Members, old => old.Key, current => current.Key, (old, current) => (old, current))
                                .Where(pair => pair.old.BodyHash == pair.current.BodyHash)
                                .Select(pair => pair.old.Key)
                                .ToHashSet(StringComparer.Ordinal);
                            if (oldMembers.ResidualHash != sourceMembers.ResidualHash
                                || earlierKeys.Any(key => key != "*" && !unchanged.Contains(key)))
                            {
                                _ = memberKeys.Add("*");
                            }

                            memberKeys.UnionWith(earlierKeys.Where(key => key == "*" || unchanged.Contains(key)));
                            spans = spans.Concat(oldMembers.RemapUnchangedSpans(sourceMembers, earlierSpans));
                        }
                    }
                    else
                    {
                        _ = memberKeys.Add("*");
                    }
                }

                if (sourceMembers.Members.Count > 0 && memberKeys.Count == 0 && file.Spans.Count > 0)
                {
                    _ = memberKeys.Add("*");
                }

                pSnapshotHash.Value = hash;
                pSnapshotContent.Value = file.Content;
                _ = await snapshotCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                pPath.Value = file.FilePath;
                pHash.Value = hash;
                pSpans.Value = LineSpan.Format(LineSpan.Normalize(spans));
                pMembers.Value = JsonSerializer.Serialize(memberKeys.Order(StringComparer.Ordinal));
                _ = await fileCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var pruneCmd = Command(transaction, """
                DELETE FROM impact_snapshots
                WHERE NOT EXISTS (SELECT 1 FROM impact_unit_files f WHERE f.snapshot_hash = impact_snapshots.snapshot_hash)
                  AND NOT EXISTS (SELECT 1 FROM coverage_files c WHERE c.snapshot_hash = impact_snapshots.snapshot_hash);
                """))
            {
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

    public ImpactSelection SelectTests(IReadOnlyList<ChangedFile> changes)
    {
        using var gate = Enter();

        var finest = ReadFinestCurrentGranularity();
        var unitTests = new Dictionary<long, IReadOnlyList<string>>();
        var snapshots = new Dictionary<string, (string? Content, IReadOnlyList<SnapshotDiff.CodeItem>? Items)>(StringComparer.Ordinal);
        var selected = new Dictionary<string, List<ImpactReason>>(StringComparer.Ordinal);
        var unmapped = new List<string>();
        var unchanged = new List<string>();

        foreach (var change in changes)
        {
            var rows = ReadFileUnits(change.FilePath);
            if (rows.Count == 0)
            {
                unmapped.Add(change.FilePath);
                continue;
            }

            var recordedMemberKeys = rows
                .SelectMany(row => row.MemberKeys)
                .Where(key => key != "*")
                .ToHashSet(StringComparer.Ordinal);
            var currentHash = change.Content is null ? null : ImpactContent.Hash(change.Content);
            var currentCodeItems = change.Content is null ? null : SnapshotDiff.CodeItems(change.Content);
            var currentMembers = change.Content is null || !change.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? null
                : SourceMembers.Parse(change.Content);
            var hits = new List<(FileUnit Unit, ImpactReason Reason)>();
            var touched = new List<(FileUnit Unit, ImpactReason Reason)>();
            var hasCodeChange = false;

            foreach (var row in rows)
            {
                if (string.Equals(row.Hash, currentHash, StringComparison.Ordinal))
                {
                    // The unit was measured against this content already.
                    continue;
                }

                if (currentMembers is { Members.Count: > 0 })
                {
                    var oldContent = ReadSnapshot(row.Hash, null);
                    if (oldContent is null)
                    {
                        hasCodeChange = true;
                        touched.Add((row, new ImpactReason(change.FilePath, new LineSpan(1, 1), null, row.Granularity, row.ProjectPath)));
                        continue;
                    }

                    var oldMembers = SourceMembers.Parse(oldContent);
                    if (oldMembers.HasSameCode(currentMembers))
                    {
                        continue;
                    }

                    hasCodeChange = true;
                    var oldByKey = oldMembers.Members.ToDictionary(member => member.Key, StringComparer.Ordinal);
                    var newByKey = currentMembers.Members.ToDictionary(member => member.Key, StringComparer.Ordinal);
                    var residualChanged = oldMembers.ResidualHash != currentMembers.ResidualHash;
                    var changedKeys = oldMembers.Members
                        .Where(member => !newByKey.TryGetValue(member.Key, out var currentMember)
                            || member.BodyHash != currentMember.BodyHash)
                        .Select(member => member.Key)
                        .Concat(currentMembers.Members
                            .Where(member => !oldByKey.TryGetValue(member.Key, out var oldMember)
                                || member.BodyHash != oldMember.BodyHash)
                            .Select(member => member.Key));
                    if (residualChanged || changedKeys.Any(key => !recordedMemberKeys.Contains(key)))
                    {
                        hits.Add((row, new ImpactReason(change.FilePath, new LineSpan(1, Math.Max(1, SnapshotDiff.SplitLines(oldContent).Length)), null, row.Granularity, row.ProjectPath)));
                        continue;
                    }

                    var rowKeys = row.MemberKeys;
                    if (rowKeys.Contains("*", StringComparer.Ordinal))
                    {
                        hits.Add((row, new ImpactReason(change.FilePath, new LineSpan(1, Math.Max(1, SnapshotDiff.SplitLines(oldContent).Length)), null, row.Granularity, row.ProjectPath)));
                        continue;
                    }

                    foreach (var key in rowKeys)
                    {
                        if (!oldByKey.TryGetValue(key, out var oldMember))
                        {
                            continue;
                        }

                        if (newByKey.TryGetValue(key, out var newMember) && oldMember.BodyHash == newMember.BodyHash)
                        {
                            continue;
                        }

                        var reason = new ImpactReason(change.FilePath, oldMember.Lines, oldMember.Lines, row.Granularity, row.ProjectPath, key);
                        hits.Add((row, reason));
                    }

                    continue;
                }

                var spans = LineSpan.Parse(row.Spans);
                LineSpan changed;
                if (change.Content is null)
                {
                    hasCodeChange = true;
                    changed = new LineSpan(1, spans.Count == 0 ? 1 : spans[^1].End);
                }
                else
                {
                    if (!snapshots.TryGetValue(row.Hash, out var snapshot))
                    {
                        var oldContent = ReadSnapshot(row.Hash, null);
                        snapshots[row.Hash] = snapshot = (
                            oldContent,
                            oldContent is null ? null : SnapshotDiff.CodeItems(oldContent));
                    }

                    if (snapshot.Content is null || snapshot.Items is null || currentCodeItems is null)
                    {
                        hasCodeChange = true;
                        changed = new LineSpan(1, spans.Count == 0 ? 1 : spans[^1].End);
                    }
                    else if (SnapshotDiff.ComputeCodeRange(snapshot.Content, snapshot.Items, currentCodeItems) is { } changedRange)
                    {
                        hasCodeChange = true;
                        changed = changedRange;
                        if (spans.FirstOrDefault(s => s.Overlaps(changed)) is { Start: > 0 } span)
                        {
                            hits.Add((row, new ImpactReason(change.FilePath, changed, span, row.Granularity, row.ProjectPath)));
                            continue;
                        }
                    }
                    else
                    {
                        continue;
                    }
                }

                touched.Add((row, new ImpactReason(change.FilePath, changed, null, row.Granularity, row.ProjectPath)));
            }

            if (!hasCodeChange)
            {
                unchanged.Add(change.FilePath);
            }

            var fileSelected = new Dictionary<string, ImpactReason>(StringComparer.Ordinal);
            foreach (var (unit, reason) in hits)
            {
                AddTests(unit, reason);
            }

            // A change outside every executed range (a new member, an attribute, a field) can still alter
            // behavior, so every test that executed the file runs.
            if (fileSelected.Count == 0)
            {
                foreach (var (unit, reason) in hits.Concat(touched))
                {
                    AddTests(unit, reason with { ExecutedSpan = null });
                }
            }

            foreach (var (fqn, reason) in fileSelected)
            {
                if (!selected.TryGetValue(fqn, out var reasons))
                {
                    selected[fqn] = reasons = [];
                }

                reasons.Add(reason);
            }

            void AddTests(FileUnit unit, ImpactReason reason)
            {
                if (!unitTests.TryGetValue(unit.UnitId, out var tests))
                {
                    unitTests[unit.UnitId] = tests = ReadUnitTests(unit.UnitId);
                }

                foreach (var fqn in tests)
                {
                    // Current finer evidence about this test overrides a coarser recording.
                    if (finest.TryGetValue((unit.ProjectPath, fqn), out var granularity) && granularity > unit.Granularity)
                    {
                        continue;
                    }

                    if (!fileSelected.TryGetValue(fqn, out var existing) || reason.Granularity > existing.Granularity)
                    {
                        fileSelected[fqn] = reason;
                    }
                }
            }
        }

        return new ImpactSelection(
            selected.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ImpactReason>)kv.Value, StringComparer.Ordinal),
            unmapped)
        {
            UnchangedFiles = unchanged,
        };
    }

    public async Task MarkChangedAsync(IReadOnlyList<ChangedFile> changes)
    {
        using var gate = Enter();

        foreach (var change in changes)
        {
            var shouldDirty = new HashSet<long>();
            using (var readCmd = _connection!.CreateCommand())
            {
                readCmd.CommandText = """
                    SELECT u.unit_id, f.snapshot_hash, f.member_keys, s.content
                    FROM impact_unit_files f
                    JOIN impact_units u ON u.unit_id = f.unit_id
                    LEFT JOIN impact_snapshots s ON s.snapshot_hash = f.snapshot_hash
                    WHERE f.file_path = $path AND u.granularity > 0 AND u.is_dirty = 0;
                    """;
                _ = readCmd.Parameters.AddWithValue("$path", change.FilePath);
                using var reader = readCmd.ExecuteReader();
                var current = change.Content is null || !change.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : SourceMembers.Parse(change.Content);
                while (reader.Read())
                {
                    var unitId = reader.GetInt64(0);
                    var snapshotHash = reader.GetString(1);
                    if (change.Content is null || current is null)
                    {
                        _ = shouldDirty.Add(unitId);
                        continue;
                    }

                    var oldContent = reader.IsDBNull(3) ? null : reader.GetString(3);
                    if (oldContent is null)
                    {
                        _ = shouldDirty.Add(unitId);
                        continue;
                    }

                    var old = SourceMembers.Parse(oldContent);
                    if (old.ResidualHash != current.ResidualHash)
                    {
                        _ = shouldDirty.Add(unitId);
                        continue;
                    }

                    var keys = DeserializeMemberKeys(reader.GetString(2));
                    var oldByKey = old.Members.ToDictionary(member => member.Key, StringComparer.Ordinal);
                    var newByKey = current.Members.ToDictionary(member => member.Key, StringComparer.Ordinal);
                    // Added or removed declarations can change binding in an unchanged caller.
                    if (!oldByKey.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(newByKey.Keys)
                        || keys.Contains("*", StringComparer.Ordinal)
                        || keys.Any(key => !oldByKey.TryGetValue(key, out var oldMember)
                            || !newByKey.TryGetValue(key, out var newMember)
                            || oldMember.BodyHash != newMember.BodyHash))
                    {
                        _ = shouldDirty.Add(unitId);
                    }
                }
            }

            if (shouldDirty.Count > 0)
            {
                using var update = _connection!.CreateCommand();
                update.CommandText = "UPDATE impact_units SET is_dirty = 1 WHERE unit_id = $unit_id;";
                var pUnit = update.Parameters.Add("$unit_id", SqliteType.Integer);
                foreach (var unitId in shouldDirty)
                {
                    pUnit.Value = unitId;
                    _ = await update.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }
    }

    public IReadOnlyList<ImpactUnitInfo> GetUnits(string projectPath, ImpactGranularity granularity)
    {
        using var gate = Enter();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT unit_key, run_id, is_dirty, recorded_at FROM impact_units
            WHERE project_path = $project AND granularity = $granularity
            ORDER BY unit_key;
            """;
        _ = cmd.Parameters.AddWithValue("$project", projectPath);
        _ = cmd.Parameters.AddWithValue("$granularity", (int)granularity);

        var rows = new List<(string Key, long RunId, bool IsCurrent, DateTimeOffset RecordedAt)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2) == 0,
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
            }
        }

        using var testsCmd = _connection!.CreateCommand();
        testsCmd.CommandText = """
            SELECT u.unit_key, t.test_fqn FROM impact_units u
            JOIN impact_unit_tests t ON t.unit_id = u.unit_id
            WHERE u.project_path = $project AND u.granularity = $granularity;
            """;
        _ = testsCmd.Parameters.AddWithValue("$project", projectPath);
        _ = testsCmd.Parameters.AddWithValue("$granularity", (int)granularity);
        var tests = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using (var reader = testsCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = reader.GetString(0);
                if (!tests.TryGetValue(key, out var list))
                {
                    tests[key] = list = [];
                }

                list.Add(reader.GetString(1));
            }
        }

        return [.. rows.Select(r => new ImpactUnitInfo(
            r.Key, r.RunId, r.IsCurrent, r.RecordedAt,
            tests.TryGetValue(r.Key, out var list) ? [.. list.Order(StringComparer.Ordinal)] : []))];
    }

    public async Task RemoveUnitsAsync(string projectPath, ImpactGranularity granularity, IReadOnlyCollection<string> unitKeys)
    {
        if (unitKeys.Count == 0)
        {
            return;
        }

        using var gate = Enter();

        using var transaction = _connection!.BeginTransaction();
        try
        {
            using (var cmd = Command(transaction, """
                DELETE FROM impact_unit_tests WHERE unit_id IN (
                    SELECT unit_id FROM impact_units WHERE project_path = $project AND granularity = $granularity AND unit_key = $key);
                DELETE FROM impact_unit_files WHERE unit_id IN (
                    SELECT unit_id FROM impact_units WHERE project_path = $project AND granularity = $granularity AND unit_key = $key);
                DELETE FROM impact_units WHERE project_path = $project AND granularity = $granularity AND unit_key = $key;
                """))
            {
                _ = cmd.Parameters.AddWithValue("$project", projectPath);
                _ = cmd.Parameters.AddWithValue("$granularity", (int)granularity);
                var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
                foreach (var key in unitKeys)
                {
                    pKey.Value = key;
                    _ = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            using (var pruneCmd = Command(transaction, """
                DELETE FROM impact_snapshots
                WHERE NOT EXISTS (SELECT 1 FROM impact_unit_files f WHERE f.snapshot_hash = impact_snapshots.snapshot_hash)
                  AND NOT EXISTS (SELECT 1 FROM coverage_files c WHERE c.snapshot_hash = impact_snapshots.snapshot_hash);
                """))
            {
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

    public IReadOnlyList<ImpactExecutor> GetTestsExecuting(string filePath)
    {
        using var gate = Enter();

        var finest = ReadFinestCurrentGranularity();
        var currentContent = ImpactContent.TryRead(filePath);
        var currentHash = currentContent is null ? null : ImpactContent.Hash(currentContent);
        var currentItems = currentContent is null ? null : SnapshotDiff.CodeItems(currentContent);
        var currentMembers = currentContent is null || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? null
            : SourceMembers.Parse(currentContent);
        var snapshotItems = new Dictionary<string, IReadOnlyList<SnapshotDiff.CodeItem>>(StringComparer.Ordinal);
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT u.project_path, t.test_fqn, u.granularity, u.is_dirty, f.snapshot_hash, f.spans, s.content, f.member_keys
            FROM impact_unit_files f
            JOIN impact_units u ON u.unit_id = f.unit_id
            JOIN impact_unit_tests t ON t.unit_id = u.unit_id
            LEFT JOIN impact_snapshots s ON s.snapshot_hash = f.snapshot_hash
            WHERE f.file_path = $path;
            """;
        _ = cmd.Parameters.AddWithValue("$path", filePath);

        var best = new Dictionary<(string Project, string Fqn), ImpactExecutor>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = (reader.GetString(0), reader.GetString(1));
            var granularity = (ImpactGranularity)reader.GetInt32(2);

            // Current finer evidence about this test overrides a coarser recording.
            if (finest.TryGetValue(key, out var finer) && finer > granularity)
            {
                continue;
            }

            var snapshotHash = reader.GetString(4);
            var snapshotContent = reader.IsDBNull(6) ? null : reader.GetString(6);
            var contentIsCurrent = string.Equals(snapshotHash, currentHash, StringComparison.Ordinal);
            var spans = LineSpan.Parse(reader.GetString(5));
            if (!contentIsCurrent && currentMembers is { Members.Count: > 0 } && snapshotContent is not null)
            {
                var oldMembers = SourceMembers.Parse(snapshotContent);
                var keys = DeserializeMemberKeys(reader.GetString(7));
                var oldByKey = oldMembers.Members.ToDictionary(member => member.Key, StringComparer.Ordinal);
                var newByKey = currentMembers.Members.ToDictionary(member => member.Key, StringComparer.Ordinal);
                contentIsCurrent = oldMembers.ResidualHash == currentMembers.ResidualHash
                    && oldByKey.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(newByKey.Keys)
                    && !keys.Contains("*", StringComparer.Ordinal)
                    && keys.All(key => oldByKey.TryGetValue(key, out var oldMember)
                        && newByKey.TryGetValue(key, out var newMember)
                        && oldMember.BodyHash == newMember.BodyHash);
                spans = oldMembers.RemapUnchangedSpans(currentMembers, spans);
            }
            else if (!contentIsCurrent && currentItems is not null && snapshotContent is not null)
            {
                if (!snapshotItems.TryGetValue(snapshotHash, out var items))
                {
                    snapshotItems[snapshotHash] = items = SnapshotDiff.CodeItems(snapshotContent);
                }

                contentIsCurrent = SnapshotDiff.ComputeCodeRange(snapshotContent, items, currentItems) is null;
            }

            if (currentMembers is not { Members.Count: > 0 } && contentIsCurrent
                && !string.Equals(snapshotHash, currentHash, StringComparison.Ordinal)
                && snapshotContent is not null
                && currentContent is not null
                && SnapshotDiff.Compute(SnapshotDiff.SplitLines(snapshotContent), SnapshotDiff.SplitLines(currentContent)) is { } lineDiff)
            {
                spans = lineDiff.Remap(spans);
            }

            var row = new ImpactExecutor(
                key.Item1, key.Item2, granularity, spans, snapshotHash,
                reader.GetInt64(3) == 0, contentIsCurrent);
            if (!best.TryGetValue(key, out var existing)
                || (row.IsUnitCurrent, row.Granularity).CompareTo((existing.IsUnitCurrent, existing.Granularity)) > 0)
            {
                best[key] = row;
            }
        }

        return [.. best.Values
            .OrderBy(e => e.ProjectPath, StringComparer.Ordinal)
            .ThenBy(e => e.TestFqn, StringComparer.Ordinal)];
    }

    private sealed record FileUnit(long UnitId, string ProjectPath, ImpactGranularity Granularity, string Hash, string Spans, IReadOnlyList<string> MemberKeys);

    private static IReadOnlyList<string> MembersForSpans(SourceMembers members, IReadOnlyList<LineSpan> spans) =>
        members.Members
            .Where(member => spans.Any(span => member.Lines.Overlaps(span)))
            .Select(member => member.Key)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool HasLooseSpans(SourceMembers members, IReadOnlyList<LineSpan> spans)
    {
        var memberLines = LineSpan.Normalize(members.Members.Select(member => member.Lines));
        return spans.Any(span => !memberLines.Any(memberSpan => memberSpan.Start <= span.Start && span.End <= memberSpan.End));
    }

    private static IReadOnlyList<string> DeserializeMemberKeys(string value) =>
        JsonSerializer.Deserialize<string[]>(value) ?? [];

    private IReadOnlyList<string> ReadMemberKeys(long unitId, string filePath, SqliteTransaction transaction)
    {
        using var cmd = Command(transaction, "SELECT member_keys FROM impact_unit_files WHERE unit_id = $unit_id AND file_path = $path;");
        _ = cmd.Parameters.AddWithValue("$unit_id", unitId);
        _ = cmd.Parameters.AddWithValue("$path", filePath);
        var value = cmd.ExecuteScalar() as string;
        return value is null ? [] : DeserializeMemberKeys(value);
    }

    private List<FileUnit> ReadFileUnits(string filePath)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT u.unit_id, u.project_path, u.granularity, f.snapshot_hash, f.spans, f.member_keys
            FROM impact_unit_files f
            JOIN impact_units u ON u.unit_id = f.unit_id
            WHERE f.file_path = $path;
            """;
        _ = cmd.Parameters.AddWithValue("$path", filePath);

        var rows = new List<FileUnit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new FileUnit(
                reader.GetInt64(0),
                reader.GetString(1),
                (ImpactGranularity)reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                DeserializeMemberKeys(reader.GetString(5))));
        }

        return rows;
    }

    /// <summary>For each test with current class- or test-level evidence, the finest such granularity.</summary>
    private Dictionary<(string Project, string Fqn), ImpactGranularity> ReadFinestCurrentGranularity()
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT u.project_path, t.test_fqn, MAX(u.granularity)
            FROM impact_unit_tests t
            JOIN impact_units u ON u.unit_id = t.unit_id
            WHERE u.granularity > 0 AND u.is_dirty = 0
            GROUP BY u.project_path, t.test_fqn;
            """;

        var finest = new Dictionary<(string, string), ImpactGranularity>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            finest[(reader.GetString(0), reader.GetString(1))] = (ImpactGranularity)reader.GetInt32(2);
        }

        return finest;
    }

    private List<string> ReadUnitTests(long unitId)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT test_fqn FROM impact_unit_tests WHERE unit_id = $unit_id;";
        _ = cmd.Parameters.AddWithValue("$unit_id", unitId);

        var tests = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tests.Add(reader.GetString(0));
        }

        return tests;
    }

    private async Task PruneRenamedTestsAsync(long unitId, IReadOnlyList<string> ranTests, SqliteTransaction transaction)
    {
        var ran = ranTests.ToHashSet(StringComparer.Ordinal);
        var methods = ranTests.Select(TestSelectionFilter.MethodName).ToHashSet(StringComparer.Ordinal);

        var stale = new List<string>();
        using (var readCmd = Command(transaction, "SELECT test_fqn FROM impact_unit_tests WHERE unit_id = $unit_id;"))
        {
            _ = readCmd.Parameters.AddWithValue("$unit_id", unitId);
            using var reader = await readCmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var fqn = reader.GetString(0);
                if (!ran.Contains(fqn) && methods.Contains(TestSelectionFilter.MethodName(fqn)))
                {
                    stale.Add(fqn);
                }
            }
        }

        using var deleteCmd = Command(transaction, "DELETE FROM impact_unit_tests WHERE unit_id = $unit_id AND test_fqn = $fqn;");
        _ = deleteCmd.Parameters.AddWithValue("$unit_id", unitId);
        var pFqn = deleteCmd.Parameters.Add("$fqn", SqliteType.Text);
        foreach (var fqn in stale)
        {
            pFqn.Value = fqn;
            _ = await deleteCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private (string Hash, string Spans)? ReadUnitFile(long unitId, string filePath, SqliteTransaction transaction)
    {
        using var cmd = Command(transaction, "SELECT snapshot_hash, spans FROM impact_unit_files WHERE unit_id = $unit_id AND file_path = $path;");
        _ = cmd.Parameters.AddWithValue("$unit_id", unitId);
        _ = cmd.Parameters.AddWithValue("$path", filePath);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private string? ReadSnapshot(string hash, SqliteTransaction? transaction)
    {
        using var cmd = Command(transaction, "SELECT content FROM impact_snapshots WHERE snapshot_hash = $hash;");
        _ = cmd.Parameters.AddWithValue("$hash", hash);
        return cmd.ExecuteScalar() as string;
    }

    private SqliteCommand Command(SqliteTransaction? transaction, string sql)
    {
        var cmd = _connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd;
    }
}
