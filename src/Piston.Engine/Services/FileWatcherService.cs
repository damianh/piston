// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

public sealed class FileWatcherService : IFileWatcherService
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly TimeSpan _debounceInterval;
    private readonly TimeProvider _timeProvider;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, FileSystemWatcher> _directoryWatchers = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Lock _watcherLock = new();
    private volatile string? _solutionDirectory;

    // The scope last requested, kept across restarts, and the one in force for the watched directory.
    private SourceInputScope? _requestedScope;
    private volatile SourceInputScope? _scope;
    private ITimer? _debounceTimer;
    private long _debounceGeneration;
    private string? _monitoringError;

    // Batches removed from _pendingEvents whose FileChanged handlers have not yet returned.
    private int _inFlightDispatches;
    private readonly Dictionary<string, FileChangeEvent> _pendingEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public event Action<FileChangeBatch>? FileChanged;

    public string? MonitoringError
    {
        get
        {
            lock (_lock)
            {
                return _monitoringError;
            }
        }
    }

    public bool HasPendingChanges
    {
        get
        {
            lock (_lock)
            {
                return _pendingEvents.Count > 0 || _inFlightDispatches > 0;
            }
        }
    }

    public FileWatcherService(TimeSpan? debounceInterval = null)
        : this(debounceInterval ?? TimeSpan.FromMilliseconds(300), TimeProvider.System)
    {
    }

    internal FileWatcherService(TimeSpan debounceInterval, TimeProvider timeProvider)
    {
        _debounceInterval = debounceInterval;
        _timeProvider = timeProvider;
    }

    public void Start(string solutionDirectory)
    {
        Stop();

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(solutionDirectory));
        lock (_watcherLock)
        {
            _solutionDirectory = root;
            _scope = EffectiveScope(root);
        }

        AddDirectoryWatchers(root);

        for (var directory = Directory.GetParent(root);
             directory is not null;
             directory = directory.Parent)
        {
            AddWatcher(directory.FullName, includeSubdirectories: false, filters: SourceFingerprinter.AncestorInputNames);
        }
    }

    public void UpdateScope(SourceInputScope? scope)
    {
        List<FileSystemWatcher> removed = [];
        string? root;
        lock (_watcherLock)
        {
            _requestedScope = scope;
            root = _solutionDirectory;
            if (root is null)
            {
                return;
            }

            var effective = EffectiveScope(root);
            _scope = effective;
            foreach (var (directory, watcher) in _directoryWatchers.ToArray())
            {
                if (!effective.MayContainInputs(directory))
                {
                    _ = _directoryWatchers.Remove(directory);
                    _ = _watchers.Remove(watcher);
                    removed.Add(watcher);
                }
            }
        }

        foreach (var watcher in removed)
        {
            watcher.Dispose();
        }

        DiagnosticLog.Instance?.Write("FileWatcher",
            $"Watching {(scope?.IsProjectScoped == true ? "project-scoped" : "all")} source inputs under {root}; " +
            $"stopped {removed.Count} directory watchers.");
        AddDirectoryWatchers(root);
    }

    private SourceInputScope EffectiveScope(string root) =>
        _requestedScope is { } requested && string.Equals(requested.Root, root, PathComparison)
            ? requested
            : SourceInputScope.ForDirectory(root);

    public void Stop()
    {
        FileSystemWatcher[] watchers;
        lock (_watcherLock)
        {
            _solutionDirectory = null;
            _scope = null;
            watchers = _watchers.ToArray();
            _watchers.Clear();
            _directoryWatchers.Clear();
        }

        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        lock (_lock)
        {
            _debounceGeneration++;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _pendingEvents.Clear();
            _monitoringError = null;
        }
    }

    // Watcher callbacks run on thread-pool threads, where an unhandled exception ends the process.
    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            HandleFileSystemEvent(e);
        }
        catch (Exception ex)
        {
            ReportMonitoringError(ex);
        }
    }

    private void HandleFileSystemEvent(FileSystemEventArgs e)
    {
        if (e.ChangeType == WatcherChangeTypes.Created && Directory.Exists(e.FullPath))
        {
            AddDirectoryWatchers(e.FullPath);
        }
        else if (e.ChangeType == WatcherChangeTypes.Deleted)
        {
            RemoveDirectoryWatchers(e.FullPath);
        }

        // Windows reports directory timestamp changes when a build creates bin/obj entries beneath a project.
        // The fingerprint contains files, not directory metadata; their own events cover real source changes.
        if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath))
        {
            return;
        }

        if (!IsFingerprintInput(e.FullPath))
        {
            return;
        }

        ScheduleDebounce(new FileChangeEvent(e.FullPath, e.ChangeType, _timeProvider.GetUtcNow()));
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            HandleRenamed(e);
        }
        catch (Exception ex)
        {
            ReportMonitoringError(ex);
        }
    }

    private void HandleRenamed(RenamedEventArgs e)
    {
        RemoveDirectoryWatchers(e.OldFullPath);
        if (Directory.Exists(e.FullPath))
        {
            AddDirectoryWatchers(e.FullPath);
        }

        if (!IsFingerprintInput(e.FullPath) && !IsFingerprintInput(e.OldFullPath))
        {
            return;
        }

        var path = IsFingerprintInput(e.FullPath) ? e.FullPath : e.OldFullPath;
        ScheduleDebounce(new FileChangeEvent(path, e.ChangeType, _timeProvider.GetUtcNow()));
    }

    private void AddWatcher(string directory, bool includeSubdirectories, IEnumerable<string>? filters = null)
    {
        Exception? failure = null;
        lock (_watcherLock)
        {
            if (TryStartWatcher(directory, includeSubdirectories, filters, out var watcher, out failure))
            {
                _watchers.Add(watcher);
            }
        }

        if (failure is not null)
        {
            ReportMonitoringError(failure);
        }
    }

    /// <summary>
    /// Starts a watcher on <paramref name="directory"/>. A directory that vanished first is not a failure:
    /// its parent's watcher reports the deletion.
    /// </summary>
    private bool TryStartWatcher(string directory, bool includeSubdirectories, IEnumerable<string>? filters,
        [NotNullWhen(true)] out FileSystemWatcher? watcher, out Exception? failure)
    {
        failure = null;
        watcher = null;
        try
        {
            watcher = CreateWatcher(directory, includeSubdirectories, filters);
            watcher.EnableRaisingEvents = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or Win32Exception)
        {
            watcher?.Dispose();
            watcher = null;
            if (Directory.Exists(directory))
            {
                failure = ex;
            }

            return false;
        }
    }

    private FileSystemWatcher CreateWatcher(string directory, bool includeSubdirectories, IEnumerable<string>? filters)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            Filter = "*",
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            InternalBufferSize = 64 * 1024,
        };

        if (filters is not null)
        {
            watcher.Filters.Clear();
            foreach (var filter in filters)
            {
                watcher.Filters.Add(filter);
            }
        }

        watcher.Changed += OnFileSystemEvent;
        watcher.Created += OnFileSystemEvent;
        watcher.Deleted += OnFileSystemEvent;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnWatcherError;
        return watcher;
    }

    private void AddDirectoryWatchers(string directory)
    {
        SourceInputScope? scope;
        string? root;
        lock (_watcherLock)
        {
            root = _solutionDirectory;
            scope = _scope;
        }

        if (root is null || scope is null)
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(directory));
        while (pending.TryPop(out var current))
        {
            if (!Directory.Exists(current) || !scope.MayContainInputs(current))
            {
                continue;
            }

            Exception? failure = null;
            var watching = true;
            lock (_watcherLock)
            {
                // A restart or scope change re-walks the tree itself.
                if (!string.Equals(_solutionDirectory, root, PathComparison) || !ReferenceEquals(_scope, scope))
                {
                    return;
                }

                if (!_directoryWatchers.ContainsKey(current))
                {
                    watching = TryStartWatcher(current, includeSubdirectories: false, filters: null,
                        out var watcher, out failure);
                    if (watching)
                    {
                        _directoryWatchers.Add(current, watcher!);
                        _watchers.Add(watcher!);
                    }
                }
            }

            if (failure is not null)
            {
                ReportMonitoringError(failure);
            }

            if (!watching)
            {
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Directory.Exists(current))
                {
                    ReportMonitoringError(ex);
                }
            }
        }
    }

    private void RemoveDirectoryWatchers(string directory)
    {
        var path = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        List<FileSystemWatcher> removed = [];
        lock (_watcherLock)
        {
            foreach (var watched in _directoryWatchers.Keys
                         .Where(candidate => IsUnderDirectory(path, candidate, comparison))
                         .ToArray())
            {
                var watcher = _directoryWatchers[watched];
                _ = _directoryWatchers.Remove(watched);
                _ = _watchers.Remove(watcher);
                removed.Add(watcher);
            }
        }

        foreach (var watcher in removed)
        {
            watcher.Dispose();
        }
    }

    private static bool IsUnderDirectory(string directory, string candidate, StringComparison comparison)
    {
        if (string.Equals(directory, candidate, comparison))
        {
            return true;
        }

        var relative = Path.GetRelativePath(directory, candidate);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison);
    }

    private bool IsFingerprintInput(string fullPath)
    {
        if (_solutionDirectory is not { } root || _scope is not { } scope)
        {
            return false;
        }

        if (scope.Covers(fullPath))
        {
            return true;
        }

        // Deleted or renamed ancestor inputs no longer satisfy CoversPath's existence check.
        return SourceFingerprinter.AncestorInputNames.Any(name =>
            string.Equals(name, Path.GetFileName(fullPath), PathComparison))
            && IsAncestorDirectory(root, Path.GetDirectoryName(fullPath), PathComparison);
    }

    private static bool IsAncestorDirectory(string root, string? candidate, StringComparison comparison)
    {
        for (var directory = Directory.GetParent(root); directory is not null; directory = directory.Parent)
        {
            if (string.Equals(directory.FullName, candidate, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        try
        {
            // Windows fails a directory's watcher (often with "Access is denied") when the directory is deleted,
            // frequently after the parent's Deleted event already stopped tracking it. The parent's watcher
            // reports the deletion; a directory outside the inputs needs no watching.
            if (sender is FileSystemWatcher watcher && IsStaleWatcher(watcher, out var directory))
            {
                DiagnosticLog.Instance?.Write("FileWatcher",
                    $"Stopped watching {directory}: {e.GetException().Message}");
                RemoveDirectoryWatchers(directory);
                return;
            }

            ReportMonitoringError(e.GetException());
        }
        catch (Exception ex)
        {
            ReportMonitoringError(ex);
        }
    }

    private bool IsStaleWatcher(FileSystemWatcher watcher, out string directory)
    {
        directory = watcher.Path;
        bool tracked;
        bool isDirectoryWatcher;
        string? root;
        lock (_watcherLock)
        {
            tracked = _watchers.Contains(watcher);
            isDirectoryWatcher = _directoryWatchers.TryGetValue(directory, out var current)
                && ReferenceEquals(current, watcher);
            root = _solutionDirectory;
        }

        if (root is null || !tracked)
        {
            return true;
        }

        return isDirectoryWatcher
            && !string.Equals(directory, root, PathComparison)
            && (!Directory.Exists(directory) || _scope?.MayContainInputs(directory) != true);
    }

    internal void ReportMonitoringError(Exception error)
    {
        lock (_lock)
        {
            if (_solutionDirectory is not { } root)
            {
                return;
            }

            _monitoringError = $"Source monitoring failed: {error.GetType().Name}: {error.Message}. Restart the daemon before verifying.";
            DiagnosticLog.Instance?.Write("FileWatcher", _monitoringError);
            ScheduleDebounce(new FileChangeEvent(root, WatcherChangeTypes.All, _timeProvider.GetUtcNow()));
        }
    }

    internal void ScheduleDebounce(FileChangeEvent evt)
    {
        lock (_lock)
        {
            // Deduplicate: same file path keeps the latest event
            _pendingEvents[evt.FilePath] = evt;
            _debounceTimer?.Dispose();
            _debounceTimer = _timeProvider.CreateTimer(
                FireDebounced, ++_debounceGeneration, _debounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void FireDebounced(object? state)
    {
        FileChangeBatch? batch;
        lock (_lock)
        {
            // Disposing a timer does not cancel callbacks already queued on the thread pool.
            if (state is not long generation || generation != _debounceGeneration)
            {
                return;
            }

            if (_pendingEvents.Count == 0)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                return;
            }

            var changes = _pendingEvents.Values.ToList();
            var timestamp = changes.Max(e => e.Timestamp);
            batch = new FileChangeBatch(changes, timestamp);
            _pendingEvents.Clear();
            _inFlightDispatches++;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        try
        {
            batch = Filter(batch);
            if (batch is not null)
            {
                FileChanged?.Invoke(batch);
            }
        }
        finally
        {
            lock (_lock)
            {
                _inFlightDispatches--;
            }
        }
    }

    /// <summary>
    /// Drops changes that are no longer inputs (the scope narrowed after they were queued) and project-directory
    /// files git ignores, which a project-scoped fingerprint excludes. Null when nothing remains.
    /// </summary>
    private FileChangeBatch? Filter(FileChangeBatch batch)
    {
        if (_solutionDirectory is not { } root || _scope is not { } scope)
        {
            return batch;
        }

        var kept = batch.Changes
            .Where(change => change.ChangeType == WatcherChangeTypes.All || IsFingerprintInput(change.FilePath))
            .ToList();
        var candidates = kept
            .Where(change => change.ChangeType != WatcherChangeTypes.All && scope.IsProjectDirectoryOnlyInput(change.FilePath))
            .ToDictionary(
                change => Path.GetRelativePath(root, change.FilePath).Replace(Path.DirectorySeparatorChar, '/'),
                change => change,
                StringComparer.Ordinal);
        if (candidates.Count > 0 && GitWorkTree.FindIgnored(root, [.. candidates.Keys]) is { Count: > 0 } ignored)
        {
            var ignoredChanges = candidates
                .Where(pair => ignored.Contains(pair.Key))
                .Select(pair => pair.Value)
                .ToHashSet();
            _ = kept.RemoveAll(ignoredChanges.Contains);
        }

        if (kept.Count < batch.Changes.Count)
        {
            DiagnosticLog.Instance?.Write("FileWatcher",
                $"Ignored {batch.Changes.Count - kept.Count} changes outside the source inputs, e.g. " +
                batch.Changes.First(change => !kept.Contains(change)).FilePath);
        }

        return kept.Count == 0 ? null : new FileChangeBatch(kept, kept.Max(change => change.Timestamp));
    }

    public void Dispose() => Stop();
}
