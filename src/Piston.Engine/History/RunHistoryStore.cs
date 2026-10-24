// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Piston.Engine.Execution;
using Piston.Engine.Models;

namespace Piston.Engine.History;

/// <summary>
/// Persistent per-test results and recent runs of one solution in one worktree, used to classify failures
/// as new, pre-existing or flaky. One process writes the file at a time (an exclusive <c>.lock</c> next to
/// it); a second process loads it read-only and keeps its own records in memory. Saves are debounced and
/// atomic (temp file, then replace).
/// </summary>
internal sealed class RunHistoryStore : IDisposable
{
    public const int FormatVersion = 1;

    /// <summary>Results kept per test, which is also the window flaky detection looks at.</summary>
    public const int MaxEntriesPerTest = 20;

    /// <summary>Runs kept with their per-test results.</summary>
    public const int MaxStoredRuns = 50;

    /// <summary>Tests without a result for this long are dropped (renamed or deleted tests).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private const int MaxMessageLength = 2048;
    private const int MaxStackTraceLength = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly Dictionary<(string Project, string Test), List<TestHistoryEntry>> _tests = [];
    private readonly List<StoredRun> _runs = []; // oldest first
    private readonly HashSet<string> _recordedRunIds = new(StringComparer.Ordinal);
    private readonly FileStream? _lock;
    private readonly Action<string>? _log;
    private readonly TimeProvider _time;
    private readonly string? _solutionPath;
    private readonly Timer _saveTimer;
    private bool _dirty;
    private bool _disposed;

    private RunHistoryStore(HistoryLocation location, string? solutionPath, FileStream? @lock, Action<string>? log, TimeProvider time)
    {
        Location = location;
        _solutionPath = solutionPath;
        _lock = @lock;
        _log = log;
        _time = time;
        _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public HistoryLocation Location { get; }

    /// <summary>Another process holds the lock: records stay in memory and are never saved.</summary>
    public bool ReadOnly => _lock is null;

    /// <summary>Delay between recording a run and saving the file.</summary>
    internal TimeSpan SaveDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Opens (or creates) the history of <paramref name="solutionPath"/> under <paramref name="baseDirectory"/>.</summary>
    /// <exception cref="InvalidOperationException">The history file would be inside the worktree.</exception>
    public static RunHistoryStore Open(string baseDirectory, string solutionPath, Action<string>? log = null, TimeProvider? time = null)
    {
        var location = HistoryLocation.Resolve(baseDirectory, solutionPath);
        var relative = Path.GetRelativePath(location.WorktreePath, location.FilePath);
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
        {
            // Inside the checkout, history writes would change the source fingerprint and make every run stale.
            throw new InvalidOperationException($"Run history must be outside the worktree; {location.FilePath} is inside {location.WorktreePath}.");
        }

        FileStream? @lock = null;
        try
        {
            CreateUserOnlyDirectory(location.Directory);
            @lock = ExclusiveFileLock.TryAcquire(location.LockPath, FileMode.OpenOrCreate);
            if (@lock is null)
            {
                log?.Invoke($"Another Piston process is writing {location.FilePath}; this one reads it and does not save.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Cannot lock run history {location.LockPath}: {ex.Message}; history is not saved.");
        }

        var store = new RunHistoryStore(location, Path.GetFullPath(solutionPath), @lock, log, time ?? TimeProvider.System);
        store.Load();
        return store;
    }

    /// <summary>
    /// Classifies the run's failed tests against the history recorded before it, then records the run.
    /// Runs without any passed, failed or skipped test are not recorded. Recording a run twice is a no-op
    /// that classifies nothing.
    /// </summary>
    public IReadOnlyList<FailureClassification> Record(TestRunRecord run)
    {
        var results = run.Suites
            .SelectMany(s => s.Tests.Select(t => (Project: s.Name, Test: t)))
            .Where(r => r.Test.Status is TestStatus.Passed or TestStatus.Failed or TestStatus.Skipped)
            .GroupBy(r => (r.Project, r.Test.ResultKey))
            .Select(g => g.Last())
            .ToList();
        if (results.Count == 0)
        {
            return [];
        }

        var at = run.CompletedAt ?? _time.GetUtcNow();
        var source = run.Freshness == ResultFreshness.Current ? run.SourceAfter?.Value : null;
        var classifications = new List<FailureClassification>();

        lock (_gate)
        {
            if (!_recordedRunIds.Add(run.RunId))
            {
                return [];
            }

            foreach (var (project, test) in results)
            {
                var key = (project, test.ResultKey);
                var entries = _tests.TryGetValue(key, out var existing) ? existing : _tests[key] = [];
                var entry = new TestHistoryEntry(run.RunId, at, test.Status, Math.Round(test.Duration.TotalMilliseconds, 3), source);
                if (test.Status == TestStatus.Failed)
                {
                    classifications.Add(Classify(project, test.ResultKey, entries, entry));
                }

                entries.Add(entry);
                if (entries.Count > MaxEntriesPerTest)
                {
                    entries.RemoveRange(0, entries.Count - MaxEntriesPerTest);
                }
            }

            _runs.Add(new StoredRun(
                new StoredRunSummary(
                    run.RunId, run.Trigger, run.Status, run.Scope, run.IsFullScope, run.Freshness, run.RequestedAt, run.CompletedAt, source,
                    results.Count(r => r.Test.Status == TestStatus.Passed),
                    results.Count(r => r.Test.Status == TestStatus.Failed),
                    results.Count(r => r.Test.Status == TestStatus.Skipped)),
                [.. results.Select(r => r.Test.Status == TestStatus.Failed
                    ? new StoredTestResult(r.Project, r.Test.ResultKey, r.Test.Status, Math.Round(r.Test.Duration.TotalMilliseconds, 3),
                        Clip(r.Test.ErrorMessage, MaxMessageLength), Clip(r.Test.StackTrace, MaxStackTraceLength))
                    : new StoredTestResult(r.Project, r.Test.ResultKey, r.Test.Status, Math.Round(r.Test.Duration.TotalMilliseconds, 3)))]));
            if (_runs.Count > MaxStoredRuns)
            {
                _runs.RemoveRange(0, _runs.Count - MaxStoredRuns);
            }

            ScheduleSaveLocked();
        }

        return classifications;
    }

    /// <summary>Recorded results of a test, matched by fully-qualified name (with or without target framework).</summary>
    /// <param name="test">Fully-qualified name.</param>
    /// <param name="project">Test project name; null matches any project.</param>
    public IReadOnlyList<TestHistory> GetTestHistory(string test, string? project = null)
    {
        lock (_gate)
        {
            return [.. _tests
                .Where(pair => (project is null || string.Equals(pair.Key.Project, project, StringComparison.OrdinalIgnoreCase))
                    && (string.Equals(pair.Key.Test, test, StringComparison.Ordinal)
                        || pair.Key.Test.StartsWith(test + " [", StringComparison.Ordinal)))
                .OrderBy(pair => pair.Key.Project, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Test, StringComparer.Ordinal)
                .Select(pair => new TestHistory(pair.Key.Project, pair.Key.Test, [.. pair.Value], FindFlakyPair(pair.Value) is not null))];
        }
    }

    /// <summary>Tests currently classified as flaky.</summary>
    public IReadOnlyList<TestHistory> GetFlakyTests()
    {
        lock (_gate)
        {
            return [.. _tests
                .Where(pair => FindFlakyPair(pair.Value) is not null)
                .OrderBy(pair => pair.Key.Project, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Test, StringComparer.Ordinal)
                .Select(pair => new TestHistory(pair.Key.Project, pair.Key.Test, [.. pair.Value], Flaky: true))];
        }
    }

    /// <summary>
    /// The classification of each test whose latest recorded result is a failure, judged against the entries
    /// before that failure, as the run that recorded it classified it.
    /// </summary>
    public IReadOnlyList<FailureClassification> GetLatestFailures()
    {
        lock (_gate)
        {
            var failures = new List<FailureClassification>();
            foreach (var (key, entries) in _tests.OrderBy(p => p.Key.Project, StringComparer.Ordinal).ThenBy(p => p.Key.Test, StringComparer.Ordinal))
            {
                var last = entries.FindLastIndex(e => e.Status is TestStatus.Passed or TestStatus.Failed);
                if (last >= 0 && entries[last].Status == TestStatus.Failed)
                {
                    failures.Add(Classify(key.Project, key.Test, entries[..last], entries[last]));
                }
            }

            return failures;
        }
    }

    /// <summary>Summaries of the stored runs, newest first.</summary>
    public IReadOnlyList<StoredRunSummary> GetRuns()
    {
        lock (_gate)
        {
            return [.. _runs.AsEnumerable().Reverse().Select(r => r.Summary)];
        }
    }

    /// <summary>A stored run with its per-test results, or null.</summary>
    public StoredRun? GetRun(string runId)
    {
        lock (_gate)
        {
            return _runs.LastOrDefault(r => r.Summary.RunId == runId);
        }
    }

    /// <summary>Classifies a failure from the entries recorded before it and the failure itself.</summary>
    internal static FailureClassification Classify(string project, string test, IReadOnlyList<TestHistoryEntry> before, TestHistoryEntry failure)
    {
        var window = before.Skip(Math.Max(0, before.Count - (MaxEntriesPerTest - 1))).Append(failure).ToList();
        if (FindFlakyPair(window) is var (passed, failed))
        {
            return new FailureClassification(project, test, FailureKind.Flaky,
                $"Passed (run {passed.RunId}, {Format(passed.At)}) and failed (run {failed.RunId}, {Format(failed.At)}) on the same source {Short(passed.Source)}.");
        }

        var decided = before.Where(e => e.Status is TestStatus.Passed or TestStatus.Failed).ToList();
        if (decided.Count == 0)
        {
            return new FailureClassification(project, test, FailureKind.Unknown, "No earlier result is recorded for this test in this worktree.");
        }

        var previous = decided[^1];
        if (previous.Status == TestStatus.Passed)
        {
            return new FailureClassification(project, test, FailureKind.New,
                $"Passed in the previous run that ran it (run {previous.RunId}, {Format(previous.At)}).");
        }

        var lastPass = decided.LastOrDefault(e => e.Status == TestStatus.Passed);
        var firstFailure = lastPass is null ? decided[0] : decided[decided.IndexOf(lastPass) + 1];
        var failures = decided.Count - (lastPass is null ? 0 : decided.IndexOf(lastPass) + 1);
        var since = $"Also failed in {failures} earlier run(s), first in run {firstFailure.RunId} ({Format(firstFailure.At)})";
        return new FailureClassification(project, test, FailureKind.PreExisting, lastPass is null
            ? $"{since}; no pass is recorded."
            : $"{since}; last passed in run {lastPass.RunId} ({Format(lastPass.At)}).");
    }

    /// <summary>A pass and a failure on the same source fingerprint, newest first; null when there is none.</summary>
    private static (TestHistoryEntry Passed, TestHistoryEntry Failed)? FindFlakyPair(IReadOnlyList<TestHistoryEntry> entries)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (e.Source is null || e.Status is not (TestStatus.Passed or TestStatus.Failed))
            {
                continue;
            }

            for (var j = i - 1; j >= 0; j--)
            {
                var other = entries[j];
                if (other.Source == e.Source && other.Status is (TestStatus.Passed or TestStatus.Failed) && other.Status != e.Status)
                {
                    return e.Status == TestStatus.Passed ? (e, other) : (other, e);
                }
            }
        }

        return null;
    }

    private static string Format(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Short(string? source) => source is null ? "" : source.Length > 12 ? source[..12] : source;

    private static string? Clip(string? text, int max) => text is { Length: > 0 } && text.Length > max ? text[..max] + "… (truncated)" : text;

    private void ScheduleSaveLocked()
    {
        _dirty = true;
        if (!ReadOnly && !_disposed)
        {
            _ = _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes the file now if anything changed and this process holds the lock.</summary>
    public void Save()
    {
        lock (_saveGate)
        {
            if (ReadOnly)
            {
                return;
            }

            FileModel model;
            lock (_gate)
            {
                if (!_dirty)
                {
                    return;
                }

                _dirty = false;
                model = ToModel();
            }

            var tmp = Location.FilePath + ".tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                {
                    JsonSerializer.Serialize(stream, model, JsonOptions);
                }

                File.Move(tmp, Location.FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"Cannot save run history {Location.FilePath}: {ex.Message}");
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // A leftover temp file is overwritten by the next save.
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _saveTimer.Dispose();
        Save();
        _lock?.Dispose();
    }

    private void Load()
    {
        if (!File.Exists(Location.FilePath))
        {
            return;
        }

        try
        {
            FileModel? model;
            using (var stream = new FileStream(Location.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
            {
                model = JsonSerializer.Deserialize<FileModel>(stream, JsonOptions);
            }

            if (model is null || model.Version != FormatVersion)
            {
                _log?.Invoke($"Ignoring run history {Location.FilePath}: unsupported format version {model?.Version}.");
                return;
            }

            FromModel(model);
            _log?.Invoke($"Loaded run history for {_tests.Count} test(s) and {_runs.Count} run(s) from {Location.FilePath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or IndexOutOfRangeException or ArgumentException)
        {
            _tests.Clear();
            _runs.Clear();
            _recordedRunIds.Clear();
            _log?.Invoke($"Cannot read run history {Location.FilePath}: {ex.Message}; starting empty.");
        }
    }

    private static void CreateUserOnlyDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = Directory.CreateDirectory(directory);
        }
        else
        {
            _ = Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // The file stores every project and test name once, in Names, and refers to them by index.
    private FileModel ToModel()
    {
        var cutoff = _time.GetUtcNow() - Retention;
        foreach (var stale in _tests.Where(pair => pair.Value.Count == 0 || pair.Value[^1].At < cutoff).Select(pair => pair.Key).ToList())
        {
            _ = _tests.Remove(stale);
        }

        var names = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        int Name(string name)
        {
            if (!index.TryGetValue(name, out var i))
            {
                i = names.Count;
                names.Add(name);
                index[name] = i;
            }

            return i;
        }

        var tests = _tests
            .OrderBy(pair => pair.Key.Project, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Test, StringComparer.Ordinal)
            .Select(pair => new TestModel
            {
                Project = Name(pair.Key.Project),
                Test = Name(pair.Key.Test),
                Runs = [.. pair.Value.Select(e => new EntryModel
                {
                    RunId = e.RunId,
                    At = e.At,
                    Status = Code(e.Status),
                    DurationMs = e.DurationMs,
                    Source = e.Source,
                })],
            })
            .ToList();

        var runs = _runs.Select(r => new RunModel
        {
            Summary = r.Summary,
            Projects = [.. r.Results.Select(t => Name(t.Project))],
            Tests = [.. r.Results.Select(t => Name(t.Test))],
            Statuses = new string([.. r.Results.Select(t => Code(t.Status))]),
            DurationsMs = [.. r.Results.Select(t => t.DurationMs)],
            Errors = r.Results
                .Select((t, i) => (t, i))
                .Where(x => x.t.ErrorMessage is not null || x.t.StackTrace is not null)
                .ToDictionary(x => x.i.ToString(CultureInfo.InvariantCulture), x => new ErrorModel { Message = x.t.ErrorMessage, StackTrace = x.t.StackTrace }),
        }).ToList();

        return new FileModel
        {
            Version = FormatVersion,
            SolutionPath = _solutionPath,
            WorktreePath = Location.WorktreePath,
            RepositoryPath = Location.RepositoryPath,
            UpdatedAt = _time.GetUtcNow(),
            Names = names,
            Tests = tests,
            Runs = runs,
        };
    }

    private void FromModel(FileModel model)
    {
        var names = model.Names ?? [];
        foreach (var test in model.Tests ?? [])
        {
            _tests[(names[test.Project], names[test.Test])] = [.. (test.Runs ?? [])
                .Select(e => new TestHistoryEntry(e.RunId, e.At, Status(e.Status), e.DurationMs, e.Source))];
        }

        foreach (var run in model.Runs ?? [])
        {
            if (run.Summary is null)
            {
                continue;
            }

            var results = new List<StoredTestResult>(run.Tests.Length);
            for (var i = 0; i < run.Tests.Length; i++)
            {
                var error = run.Errors?.GetValueOrDefault(i.ToString(CultureInfo.InvariantCulture));
                results.Add(new StoredTestResult(
                    names[run.Projects[i]], names[run.Tests[i]], Status(run.Statuses[i]), run.DurationsMs[i], error?.Message, error?.StackTrace));
            }

            _runs.Add(new StoredRun(run.Summary, results));
            _ = _recordedRunIds.Add(run.Summary.RunId);
        }

        foreach (var entry in _tests.Values.SelectMany(e => e))
        {
            _ = _recordedRunIds.Add(entry.RunId);
        }
    }

    private static char Code(TestStatus status) => status switch
    {
        TestStatus.Passed => 'P',
        TestStatus.Failed => 'F',
        TestStatus.Skipped => 'S',
        _ => 'N',
    };

    private static TestStatus Status(char code) => code switch
    {
        'P' => TestStatus.Passed,
        'F' => TestStatus.Failed,
        'S' => TestStatus.Skipped,
        _ => TestStatus.NotRun,
    };

    private sealed class FileModel
    {
        public int Version { get; set; }
        public string? SolutionPath { get; set; }
        public string? WorktreePath { get; set; }
        public string? RepositoryPath { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<string>? Names { get; set; }
        public List<TestModel>? Tests { get; set; }
        public List<RunModel>? Runs { get; set; }
    }

    private sealed class TestModel
    {
        public int Project { get; set; }
        public int Test { get; set; }
        public List<EntryModel>? Runs { get; set; }
    }

    private sealed class EntryModel
    {
        public string RunId { get; set; } = "";
        public DateTimeOffset At { get; set; }
        public char Status { get; set; }
        public double DurationMs { get; set; }
        public string? Source { get; set; }
    }

    private sealed class RunModel
    {
        public StoredRunSummary? Summary { get; set; }
        public int[] Projects { get; set; } = [];
        public int[] Tests { get; set; } = [];
        public string Statuses { get; set; } = "";
        public double[] DurationsMs { get; set; } = [];
        public Dictionary<string, ErrorModel>? Errors { get; set; }
    }

    private sealed class ErrorModel
    {
        public string? Message { get; set; }
        public string? StackTrace { get; set; }
    }
}
