using Piston.Engine.Models;

namespace Piston.Engine.Services;

public sealed class FileWatcherService : IFileWatcherService
{
    private readonly TimeSpan _debounceInterval;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly Dictionary<string, FileChangeEvent> _pendingEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public event Action<FileChangeBatch>? FileChanged;

    public FileWatcherService(TimeSpan? debounceInterval = null)
    {
        _debounceInterval = debounceInterval ?? TimeSpan.FromMilliseconds(300);
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
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _pendingEvents.Clear();
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (IsExcluded(e.FullPath))
            return;

        ScheduleDebounce(new FileChangeEvent(e.FullPath, e.ChangeType, DateTimeOffset.UtcNow));
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsExcluded(e.FullPath))
            return;

        ScheduleDebounce(new FileChangeEvent(e.FullPath, e.ChangeType, DateTimeOffset.UtcNow));
    }

    private static bool IsExcluded(string fullPath)
    {
        var normalized = fullPath.Replace('\\', '/');
        return normalized.Contains("/bin/")
            || normalized.Contains("/obj/")
            || normalized.Contains("/.git/")
            || normalized.Contains("/node_modules/");
    }

    private void ScheduleDebounce(FileChangeEvent evt)
    {
        lock (_lock)
        {
            // Deduplicate: same file path keeps the latest event
            _pendingEvents[evt.FilePath] = evt;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(FireDebounced, null, _debounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void FireDebounced(object? state)
    {
        FileChangeBatch? batch;
        lock (_lock)
        {
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
