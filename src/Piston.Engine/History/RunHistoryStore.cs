// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Piston.Engine.Execution;
using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.History;

/// <summary>
/// Persistent per-test results and recent runs of one solution in one worktree, used to classify failures
/// as new, pre-existing or flaky. Flaky detection also reads, read-only, the histories the repository's other
/// worktrees keep for the same solution. One process writes the file at a time (an exclusive <c>.lock</c> next to
/// it); a second process loads it read-only and keeps its own records in memory. Saves are debounced and
/// atomic (temp file, then replace).
/// </summary>
internal sealed class RunHistoryStore : IDisposable
{
    public const int FormatVersion = 2;

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
    private HashSet<string> _knownProjectIds = new(StringComparer.Ordinal);
    private readonly List<StoredRun> _runs = []; // oldest first
    private readonly HashSet<string> _recordedRunIds = new(StringComparer.Ordinal);
    private readonly FileStream? _lock;
    private readonly SiblingHistories _siblings;
    private readonly Action<string>? _log;
    private readonly TimeProvider _time;
    private readonly string? _solutionPath;
    private readonly Timer _saveTimer;
    private bool _dirty;
    private bool _disposed;
    private bool _legacyProjects;

    private RunHistoryStore(HistoryLocation location, string? solutionPath, FileStream? @lock, Action<string>? log, TimeProvider time)
    {
        Location = location;
        _solutionPath = solutionPath;
        _lock = @lock;
        _siblings = new SiblingHistories(location, log);
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
    /// Classifies the run's failed tests (including tests that failed and then passed on retry) against the history
    /// recorded before it and the histories of the repository's other worktrees, then records the run, one entry per
    /// attempt. Runs without any passed, failed or skipped test are not recorded. Recording a run twice is a no-op
    /// that classifies nothing.
    /// </summary>
    public IReadOnlyList<FailureClassification> Record(TestRunRecord run)
    {
        var results = run.Suites
            .SelectMany(s => s.Tests.Select(t => (Project: s.Name, ProjectId: s.Key, Test: t)))
            .Where(r => r.Test.Status is TestStatus.Passed or TestStatus.Failed or TestStatus.Skipped)
            .GroupBy(r => (r.ProjectId, r.Test.ResultKey))
            .Select(g => g.Last())
            .ToList();
        if (results.Count == 0)
        {
            return [];
        }

        var at = run.CompletedAt ?? _time.GetUtcNow();
        var source = run.Freshness == ResultFreshness.Current ? run.SourceAfter?.Value : null;
        var classifications = new List<FailureClassification>();
        var siblings = SiblingSnapshot();

        lock (_gate)
        {
            if (_legacyProjects)
            {
                _log?.Invoke("Run history migration is waiting for complete project discovery; this run is not recorded.");
                return [];
            }

            if (!_recordedRunIds.Add(run.RunId))
            {
                return [];
            }

            foreach (var (project, projectId, test) in results)
            {
                var key = (projectId, test.ResultKey);
                var entries = _tests.TryGetValue(key, out var existing) ? existing : _tests[key] = [];
                var current = test.Attempts
                    .Select((a, i) => new TestHistoryEntry(run.RunId, at, a.Status, Math.Round(a.Duration.TotalMilliseconds, 3), source, i))
                    .Append(new TestHistoryEntry(run.RunId, at, test.Status, Math.Round(test.Duration.TotalMilliseconds, 3), source, test.Attempts.Count))
                    .ToList();
                if (current.Any(e => e.Status == TestStatus.Failed))
                {
                    classifications.Add(Classify(project, test.ResultKey, entries, current, siblings.GetValueOrDefault(key) ?? []) with { ProjectId = projectId });
                }

                entries.AddRange(current);
                if (entries.Count > MaxEntriesPerTest)
                {
                    entries.RemoveRange(0, entries.Count - MaxEntriesPerTest);
                }
            }

            var retried = results.Where(r => r.Test.Attempts.Count > 0).ToList();
            _runs.Add(new StoredRun(
                new StoredRunSummary(
                    run.RunId, run.Trigger, run.Status, run.Scope, run.IsFullScope, run.Freshness, run.RequestedAt, run.CompletedAt, source,
                    results.Count(r => r.Test.Status == TestStatus.Passed),
                    results.Count(r => r.Test.Status == TestStatus.Failed),
                    results.Count(r => r.Test.Status == TestStatus.Skipped),
                    retried.Count,
                    retried.Count(r => r.Test.Status == TestStatus.Passed)),
                [.. results.Select(r => ToStored(r.Project, r.Test) with { ProjectId = r.ProjectId })]));
            if (_runs.Count > MaxStoredRuns)
            {
                _runs.RemoveRange(0, _runs.Count - MaxStoredRuns);
            }

            ScheduleSaveLocked();
        }

        return classifications;
    }

    private static StoredTestResult ToStored(string project, TestResult test)
    {
        var attempts = test.Attempts.Count == 0
            ? null
            : test.Attempts
                .Select(a => new StoredAttempt(a.Status, Math.Round(a.Duration.TotalMilliseconds, 3),
                    a.Status == TestStatus.Failed ? Clip(a.ErrorMessage, MaxMessageLength) : null))
                .ToList();
        return test.Status == TestStatus.Failed
            ? new StoredTestResult(project, test.ResultKey, test.Status, Math.Round(test.Duration.TotalMilliseconds, 3),
                Clip(test.ErrorMessage, MaxMessageLength), Clip(test.StackTrace, MaxStackTraceLength), attempts)
            : new StoredTestResult(project, test.ResultKey, test.Status, Math.Round(test.Duration.TotalMilliseconds, 3), Attempts: attempts);
    }

    /// <summary>Recorded results of a test, matched by fully-qualified name (with or without target framework).</summary>
    /// <param name="test">Fully-qualified name.</param>
    /// <param name="projectId">Solution-relative test project ID; null matches any project.</param>
    public IReadOnlyList<TestHistory> GetTestHistory(string test, string? projectId = null)
    {
        var siblings = SiblingSnapshot();
        lock (_gate)
        {
            if (projectId is not null && _knownProjectIds.Count > 0 && !_knownProjectIds.Contains(projectId)
                && !_tests.Keys.Any(key => string.Equals(key.Project, projectId, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"'{projectId}' is not a test project ID in this solution.", nameof(projectId));
            }

            return [.. _tests
                .Where(pair => (projectId is null || string.Equals(pair.Key.Project, projectId, StringComparison.Ordinal))
                    && (string.Equals(pair.Key.Test, test, StringComparison.Ordinal)
                        || pair.Key.Test.StartsWith(test + " [", StringComparison.Ordinal)))
                .OrderBy(pair => pair.Key.Project, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Test, StringComparer.Ordinal)
                .Select(pair => new TestHistory(ProjectName(pair.Key.Project), pair.Key.Test, [.. pair.Value],
                    FindFlakiness(pair.Value, siblings.GetValueOrDefault(pair.Key) ?? []))
                {
                    ProjectId = pair.Key.Project,
                })];
        }
    }

    /// <summary>Tests currently classified as flaky, with the evidence.</summary>
    public IReadOnlyList<TestHistory> GetFlakyTests()
    {
        var siblings = SiblingSnapshot();
        lock (_gate)
        {
            return [.. _tests
                .OrderBy(pair => pair.Key.Project, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Test, StringComparer.Ordinal)
                .Select(pair => new TestHistory(ProjectName(pair.Key.Project), pair.Key.Test, [.. pair.Value],
                    FindFlakiness(pair.Value, siblings.GetValueOrDefault(pair.Key) ?? []))
                {
                    ProjectId = pair.Key.Project,
                })
                .Where(h => h.Flaky)];
        }
    }

    /// <summary>
    /// The classification of each test whose latest recorded result is a failure, judged against the entries
    /// before that run, together with the other worktrees' results.
    /// </summary>
    public IReadOnlyList<FailureClassification> GetLatestFailures()
    {
        var siblings = SiblingSnapshot();
        lock (_gate)
        {
            var failures = new List<FailureClassification>();
            foreach (var (key, entries) in _tests.OrderBy(p => p.Key.Project, StringComparer.Ordinal).ThenBy(p => p.Key.Test, StringComparer.Ordinal))
            {
                var last = entries.FindLastIndex(e => e.Status is TestStatus.Passed or TestStatus.Failed);
                if (last >= 0 && entries[last].Status == TestStatus.Failed)
                {
                    var runId = entries[last].RunId;
                    var first = last;
                    while (first > 0 && entries[first - 1].RunId == runId)
                    {
                        first--;
                    }

                    failures.Add(Classify(ProjectName(key.Project), key.Test, entries[..first], entries[first..(last + 1)], siblings.GetValueOrDefault(key) ?? []) with { ProjectId = key.Project });
                }
            }

            return failures;
        }
    }

    /// <summary>
    /// Resolves v1 short-name keys once the solution's test projects are available. Ambiguous or removed
    /// project names cannot safely be assigned and are discarded.
    /// </summary>
    public void ResolveLegacyProjects(IReadOnlyList<string> projectPaths, string solutionDirectory)
    {
        lock (_gate)
        {
            _knownProjectIds = projectPaths
                .Select(path => TestProjectNames.ProjectId(solutionDirectory, path))
                .ToHashSet(StringComparer.Ordinal);
            if (!_legacyProjects)
            {
                return;
            }

            var idsByName = _knownProjectIds
                .GroupBy(ProjectName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

            string? Resolve(string legacyName) =>
                idsByName.TryGetValue(legacyName, out var ids) && ids.Count == 1 ? ids[0] : null;

            var migratedTests = new Dictionary<(string Project, string Test), List<TestHistoryEntry>>();
            var dropped = 0;
            foreach (var (key, entries) in _tests)
            {
                if (Resolve(key.Project) is { } id)
                {
                    migratedTests[(id, key.Test)] = entries;
                }
                else
                {
                    dropped++;
                }
            }

            _tests.Clear();
            foreach (var (key, entries) in migratedTests)
            {
                _tests[key] = entries;
            }

            for (var i = 0; i < _runs.Count; i++)
            {
                var run = _runs[i];
                var results = new List<StoredTestResult>();
                foreach (var result in run.Results)
                {
                    if (Resolve(result.ProjectId) is { } id)
                    {
                        results.Add(result with { Project = ProjectName(id), ProjectId = id });
                    }
                    else
                    {
                        dropped++;
                    }
                }

                _runs[i] = run with { Results = results };
            }

            _legacyProjects = false;
            if (dropped > 0)
            {
                _log?.Invoke($"Dropped {dropped} legacy run-history result(s) whose project name was ambiguous or no longer exists.");
            }

            ScheduleSaveLocked();
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

    private IReadOnlyDictionary<(string Project, string Test), IReadOnlyList<SiblingEntry>> SiblingSnapshot()
    {
        try
        {
            // A removed worktree's file is never saved again, so its entries never age out on their own.
            var cutoff = _time.GetUtcNow() - Retention;
            var snapshot = _siblings.Snapshot();
            if (snapshot.Values.All(entries => entries.All(e => e.Entry.At >= cutoff)))
            {
                return snapshot;
            }

            return snapshot
                .Select(pair => (pair.Key, Entries: (IReadOnlyList<SiblingEntry>)[.. pair.Value.Where(e => e.Entry.At >= cutoff)]))
                .Where(pair => pair.Entries.Count > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Cannot read the run histories of other worktrees: {ex.Message}");
            return new Dictionary<(string Project, string Test), IReadOnlyList<SiblingEntry>>();
        }
    }

    /// <summary>Classifies a run's results of a test that failed at least once, from the entries recorded before the run.</summary>
    /// <param name="before">This worktree's entries recorded before the run, oldest first.</param>
    /// <param name="current">The run's entries, one per attempt; the last is the final result.</param>
    /// <param name="siblings">The test's results recorded by other worktrees of the repository.</param>
    internal static FailureClassification Classify(
        string project,
        string test,
        IReadOnlyList<TestHistoryEntry> before,
        IReadOnlyList<TestHistoryEntry> current,
        IReadOnlyList<SiblingEntry>? siblings = null)
    {
        var window = before.Skip(Math.Max(0, before.Count - Math.Max(0, MaxEntriesPerTest - current.Count))).Concat(current).ToList();
        if (FindFlakiness(window, siblings ?? []) is { } flakiness)
        {
            var lastPass = flakiness.Outcomes.FirstOrDefault(o => o.Status == TestStatus.Passed);
            var lastFailure = flakiness.Outcomes.FirstOrDefault(o => o.Status == TestStatus.Failed);
            var latest = lastPass is null || lastFailure is null
                ? ""
                : $" Last passed in {Describe(lastPass)}; last failed in {Describe(lastFailure)}.";
            return new FailureClassification(project, test, FailureKind.Flaky, flakiness.Describe() + latest, flakiness);
        }

        // Earlier runs are judged by their final result: a test that passed on retry passed in that run.
        var decided = before
            .Where((e, i) => (i == before.Count - 1 || before[i + 1].RunId != e.RunId) && e.Status is TestStatus.Passed or TestStatus.Failed)
            .ToList();
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

        var lastPassed = decided.LastOrDefault(e => e.Status == TestStatus.Passed);
        var firstFailure = lastPassed is null ? decided[0] : decided[decided.IndexOf(lastPassed) + 1];
        var failures = decided.Count - (lastPassed is null ? 0 : decided.IndexOf(lastPassed) + 1);
        var since = $"Also failed in {failures} earlier run(s), first in run {firstFailure.RunId} ({Format(firstFailure.At)})";
        return new FailureClassification(project, test, FailureKind.PreExisting, lastPassed is null
            ? $"{since}; no pass is recorded."
            : $"{since}; last passed in run {lastPassed.RunId} ({Format(lastPassed.At)}).");
    }

    /// <summary>
    /// Flaky evidence for a test: the newest source fingerprint on which it both passed and failed, counting this
    /// worktree's entries and other worktrees' results; otherwise the newest run whose retries both passed and failed
    /// (the same build). Null when there is neither.
    /// </summary>
    internal static FlakinessEvidence? FindFlakiness(IReadOnlyList<TestHistoryEntry> entries, IReadOnlyList<SiblingEntry> siblings)
    {
        var outcomes = entries
            .Where(e => e.Status is TestStatus.Passed or TestStatus.Failed)
            .Select(e => (e.Source, Outcome: new FlakyOutcome(e.RunId, e.At, e.Status, e.Attempt, null)))
            .Concat(siblings
                .Where(s => s.Entry.Source is not null && s.Entry.Status is TestStatus.Passed or TestStatus.Failed)
                .Select(s => (s.Entry.Source, Outcome: new FlakyOutcome(s.Entry.RunId, s.Entry.At, s.Entry.Status, s.Entry.Attempt, s.Worktree))))
            .ToList();

        static bool BothOutcomes(IEnumerable<FlakyOutcome> group) =>
            group.Any(o => o.Status == TestStatus.Passed) && group.Any(o => o.Status == TestStatus.Failed);

        var bySource = outcomes
            .Where(o => o.Source is not null)
            .GroupBy(o => o.Source!, StringComparer.Ordinal)
            .Where(g => BothOutcomes(g.Select(x => x.Outcome)))
            .OrderByDescending(g => g.Max(x => x.Outcome.At))
            .FirstOrDefault();
        if (bySource is not null)
        {
            return Evidence(bySource.Key, [.. bySource.Select(x => x.Outcome)]);
        }

        var byRun = outcomes
            .Where(o => o.Outcome.Worktree is null)
            .GroupBy(o => o.Outcome.RunId, StringComparer.Ordinal)
            .Where(g => BothOutcomes(g.Select(x => x.Outcome)))
            .OrderByDescending(g => g.Max(x => x.Outcome.At))
            .FirstOrDefault();
        return byRun is null ? null : Evidence(null, [.. byRun.Select(x => x.Outcome)]);
    }

    private static FlakinessEvidence Evidence(string? source, List<FlakyOutcome> outcomes) => new(
        source,
        outcomes.Count(o => o.Status == TestStatus.Passed),
        outcomes.Count(o => o.Status == TestStatus.Failed),
        outcomes.Count(o => o.Attempt > 0),
        [.. outcomes.Select(o => o.Worktree).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        [.. outcomes
            .OrderByDescending(o => o.At)
            .ThenByDescending(o => o.Attempt)
            .Take(FlakinessEvidence.MaxOutcomes)]);

    private static string Describe(FlakyOutcome outcome) =>
        $"run {outcome.RunId}{(outcome.Attempt > 0 ? $" retry {outcome.Attempt}" : "")}" +
        $"{(outcome.Worktree is { } worktree ? $" in worktree {worktree}" : "")} ({Format(outcome.At)})";

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

            if (model is null || model.Version is not (1 or FormatVersion))
            {
                _log?.Invoke($"Ignoring run history {Location.FilePath}: unsupported format version {model?.Version}.");
                return;
            }

            _legacyProjects = model.Version == 1;
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
                    Attempt = e.Attempt == 0 ? null : e.Attempt,
                })],
            })
            .ToList();

        var runs = _runs.Select(r => new RunModel
        {
            Summary = r.Summary,
            Projects = [.. r.Results.Select(t => Name(t.ProjectId))],
            Tests = [.. r.Results.Select(t => Name(t.Test))],
            Statuses = new string([.. r.Results.Select(t => Code(t.Status))]),
            DurationsMs = [.. r.Results.Select(t => t.DurationMs)],
            Errors = r.Results
                .Select((t, i) => (t, i))
                .Where(x => x.t.ErrorMessage is not null || x.t.StackTrace is not null)
                .ToDictionary(x => x.i.ToString(CultureInfo.InvariantCulture), x => new ErrorModel { Message = x.t.ErrorMessage, StackTrace = x.t.StackTrace }),
            Attempts = r.Results.Any(t => t.Attempts is { Count: > 0 })
                ? r.Results
                    .Select((t, i) => (t, i))
                    .Where(x => x.t.Attempts is { Count: > 0 })
                    .ToDictionary(x => x.i.ToString(CultureInfo.InvariantCulture), x => x.t.Attempts!
                        .Select(a => new AttemptModel { Status = Code(a.Status), DurationMs = a.DurationMs, Message = a.ErrorMessage })
                        .ToList())
                : null,
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
                .Select(e => new TestHistoryEntry(e.RunId, e.At, Status(e.Status), e.DurationMs, e.Source, e.Attempt ?? 0))];
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
                var attempts = run.Attempts?.GetValueOrDefault(i.ToString(CultureInfo.InvariantCulture));
                var project = names[run.Projects[i]];
                results.Add(new StoredTestResult(
                    model.Version == 1 ? project : ProjectName(project),
                    names[run.Tests[i]], Status(run.Statuses[i]), run.DurationsMs[i], error?.Message, error?.StackTrace,
                    attempts is null ? null : [.. attempts.Select(a => new StoredAttempt(Status(a.Status), a.DurationMs, a.Message))])
                {
                    ProjectId = project,
                });
            }

            _runs.Add(new StoredRun(run.Summary, results));
            _ = _recordedRunIds.Add(run.Summary.RunId);
        }

        foreach (var entry in _tests.Values.SelectMany(e => e))
        {
            _ = _recordedRunIds.Add(entry.RunId);
        }
    }

    /// <summary>
    /// Reads another worktree's history file without locking it: its worktree path and the entries that carry a
    /// source fingerprint. Throws when the file cannot be read or parsed.
    /// </summary>
    internal static (string? WorktreePath, IReadOnlyDictionary<(string Project, string Test), IReadOnlyList<TestHistoryEntry>> Tests) ReadSourcedEntries(string path)
    {
        FileModel? model;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024))
        {
            model = JsonSerializer.Deserialize<FileModel>(stream, JsonOptions);
        }

        if (model is null || model.Version != FormatVersion)
        {
            throw new InvalidDataException($"unsupported format version {model?.Version}");
        }

        var names = model.Names ?? [];
        var tests = new Dictionary<(string Project, string Test), IReadOnlyList<TestHistoryEntry>>();
        foreach (var test in model.Tests ?? [])
        {
            var entries = (test.Runs ?? [])
                .Where(e => e.Source is not null)
                .Select(e => new TestHistoryEntry(e.RunId, e.At, Status(e.Status), e.DurationMs, e.Source, e.Attempt ?? 0))
                .ToList();
            if (entries.Count > 0)
            {
                tests[(names[test.Project], names[test.Test])] = entries;
            }
        }

        return (model.WorktreePath, tests);
    }

    private static string ProjectName(string projectId) =>
        TestProjectNames.IsProjectFile(projectId) ? TestProjectNames.SuiteName(projectId) : projectId;

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
        public int? Attempt { get; set; }
    }

    private sealed class RunModel
    {
        public StoredRunSummary? Summary { get; set; }
        public int[] Projects { get; set; } = [];
        public int[] Tests { get; set; } = [];
        public string Statuses { get; set; } = "";
        public double[] DurationsMs { get; set; } = [];
        public Dictionary<string, ErrorModel>? Errors { get; set; }
        public Dictionary<string, List<AttemptModel>>? Attempts { get; set; }
    }

    private sealed class AttemptModel
    {
        public char Status { get; set; }
        public double DurationMs { get; set; }
        public string? Message { get; set; }
    }

    private sealed class ErrorModel
    {
        public string? Message { get; set; }
        public string? StackTrace { get; set; }
    }
}
