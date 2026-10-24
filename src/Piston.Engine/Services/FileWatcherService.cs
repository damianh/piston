// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

public sealed class FileWatcherService : IFileWatcherService
{
    private readonly TimeSpan _debounceInterval;
    private readonly TimeProvider _timeProvider;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, FileSystemWatcher> _directoryWatchers = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Lock _watcherLock = new();
    private string? _solutionDirectory;
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

        _solutionDirectory = Path.GetFullPath(solutionDirectory);
        AddDirectoryWatchers(_solutionDirectory);

        for (var directory = Directory.GetParent(_solutionDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            AddWatcher(directory.FullName, includeSubdirectories: false, filters: SourceFingerprinter.AncestorInputNames);
        }
    }

    public void Stop()
    {
        FileSystemWatcher[] watchers;
        lock (_watcherLock)
        {
            _solutionDirectory = null;
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

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType == WatcherChangeTypes.Created && Directory.Exists(e.FullPath))
        {
            AddDirectoryWatchers(e.FullPath);
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
        var watcher = CreateWatcher(directory, includeSubdirectories, filters);
        lock (_watcherLock)
        {
            _watchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
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
        if (_solutionDirectory is not { } root)
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(directory));
        while (pending.TryPop(out var current))
        {
            if (!Directory.Exists(current) || !SourceFingerprinter.CoversPath(root, current))
            {
                continue;
            }

            lock (_watcherLock)
            {
                if (!string.Equals(_solutionDirectory, root,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    return;
                }

                if (!_directoryWatchers.ContainsKey(current))
                {
                    var watcher = CreateWatcher(current, includeSubdirectories: false, filters: null);
                    _directoryWatchers.Add(current, watcher);
                    _watchers.Add(watcher);
                    watcher.EnableRaisingEvents = true;
                }
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    pending.Push(child);
                }
            }
            catch (DirectoryNotFoundException)
            {
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
        if (_solutionDirectory is not { } root)
        {
            return false;
        }

        if (SourceFingerprinter.CoversPath(root, fullPath))
        {
            return true;
        }

        // Deleted or renamed ancestor inputs no longer satisfy CoversPath's existence check.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return SourceFingerprinter.AncestorInputNames.Any(name =>
            string.Equals(name, Path.GetFileName(fullPath), comparison))
            && IsAncestorDirectory(root, Path.GetDirectoryName(fullPath), comparison);
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

    private void OnWatcherError(object sender, ErrorEventArgs e) => ReportMonitoringError(e.GetException());

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
            FileChanged?.Invoke(batch);
        }
        finally
        {
            lock (_lock)
            {
                _inFlightDispatches--;
            }
        }
    }

    public void Dispose() => Stop();
}
