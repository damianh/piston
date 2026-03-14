using Piston.Engine.Models;

namespace Piston.Engine.Services;

public sealed class FileWatcherService : IFileWatcherService
{
    private readonly TimeSpan _debounceInterval;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private FileChangeEvent? _pendingEvent;
    private readonly Lock _lock = new();

    public event Action<FileChangeEvent>? FileChanged;

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
            _pendingEvent = null;
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
            _pendingEvent = evt;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(FireDebounced, null, _debounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void FireDebounced(object? state)
    {
        FileChangeEvent? evt;
        lock (_lock)
        {
            evt = _pendingEvent;
            _pendingEvent = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        if (evt is not null)
            FileChanged?.Invoke(evt);
    }

    public void Dispose() => Stop();
}
