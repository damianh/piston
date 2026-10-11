// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

public sealed class FileWatcherService : IFileWatcherService
{
    private readonly TimeSpan _debounceInterval;
    private readonly TimeProvider _timeProvider;
    private FileSystemWatcher? _watcher;
    private ITimer? _debounceTimer;
    private long _debounceGeneration;
    private readonly Dictionary<string, FileChangeEvent> _pendingEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public event Action<FileChangeBatch>? FileChanged;

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

        _watcher = new FileSystemWatcher(solutionDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
        };

        _watcher.Filters.Add("*.cs");
        _watcher.Filters.Add("*.csproj");
        _watcher.Filters.Add("*.props");
        _watcher.Filters.Add("*.targets");

        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Renamed += OnRenamed;

        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;

        lock (_lock)
        {
            _debounceGeneration++;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _pendingEvents.Clear();
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (IsExcluded(e.FullPath))
            return;

        ScheduleDebounce(new FileChangeEvent(e.FullPath, e.ChangeType, _timeProvider.GetUtcNow()));
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsExcluded(e.FullPath))
            return;

        ScheduleDebounce(new FileChangeEvent(e.FullPath, e.ChangeType, _timeProvider.GetUtcNow()));
    }

    private static bool IsExcluded(string fullPath)
    {
        var normalized = fullPath.Replace('\\', '/');
        return normalized.Contains("/bin/")
            || normalized.Contains("/obj/")
            || normalized.Contains("/.git/")
            || normalized.Contains("/node_modules/");
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
                return;

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
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        FileChanged?.Invoke(batch);
    }

    public void Dispose() => Stop();
}
