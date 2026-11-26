// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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

        // Queue both endpoints: debounce filtering may drop one (e.g. a git-ignored destination) but not the other.
        foreach (var path in new[] { e.OldFullPath, e.FullPath })
        {
            if (IsFingerprintInput(path))
            {
                ScheduleDebounce(new FileChangeEvent(path, e.ChangeType, _timeProvider.GetUtcNow()));
            }
        }
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
            if (DirectoryIsPresent(directory))
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
            if (!DirectoryIsPresent(current) || !scope.MayContainInputs(current))
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
                if (DirectoryIsPresent(current))
                {
                    ReportMonitoringError(ex);
                }
            }
        }
    }

    /// <summary>
    /// Stops watching <paramref name="directory"/> and its subdirectories; when <paramref name="expected"/> is
    /// given, only if it still is the watcher of <paramref name="directory"/>, so a late error from a replaced
    /// watcher leaves the replacement alone.
    /// </summary>
    private void RemoveDirectoryWatchers(string directory, FileSystemWatcher? expected = null)
    {
        var path = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        List<FileSystemWatcher> removed = [];
        lock (_watcherLock)
        {
            if (expected is not null
                && (!_directoryWatchers.TryGetValue(path, out var current) || !ReferenceEquals(current, expected)))
            {
                return;
            }

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
            if (sender is FileSystemWatcher watcher && IsStaleWatcher(watcher, out var directory, out var tracked))
            {
                DiagnosticLog.Instance?.Write("FileWatcher",
                    $"Stopped watching {directory}: {e.GetException().Message}");
                // An untracked watcher was already removed; its directory may since have a replacement.
                if (tracked)
                {
                    RemoveDirectoryWatchers(directory, watcher);
                }

                return;
            }

            ReportMonitoringError(e.GetException());
        }
        catch (Exception ex)
        {
            ReportMonitoringError(ex);
        }
    }

    private bool IsStaleWatcher(FileSystemWatcher watcher, out string directory, out bool tracked)
    {
        directory = watcher.Path;
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
            && (!DirectoryIsPresent(directory) || _scope?.MayContainInputs(directory) != true);
    }

    /// <summary>
    /// Whether <paramref name="directory"/> exists and is not being deleted. On Windows a directory stays visible
    /// to <see cref="Directory.Exists"/> while its deletion is pending (until every open handle, such as the
    /// deleter's or a watcher's, closes), yet its watcher has already failed with "Access is denied". Opening it
    /// then fails with STATUS_DELETE_PENDING, which Win32 also reports as "Access is denied", so the NT status
    /// tells a pending deletion apart from a permission failure.
    /// </summary>
    private static bool DirectoryIsPresent(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        const uint shareAll = 0x00000007; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000; // FILE_FLAG_BACKUP_SEMANTICS, required to open a directory
        const int fileNotFound = 2;
        const int pathNotFound = 3;
        const int accessDenied = 5;
        const int statusDeletePending = unchecked((int)0xC0000056);
        using var handle = CreateFile(ExtendedPath(directory), 0, shareAll, IntPtr.Zero, openExisting,
            backupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return true;
        }

        return Marshal.GetLastPInvokeError() switch
        {
            fileNotFound or pathNotFound => false,
            accessDenied => RtlGetLastNtStatus() != statusDeletePending,
            _ => true,
        };
    }

    // Lets the native open handle paths longer than MAX_PATH without the process opting in to long paths.
    private static string ExtendedPath(string directory)
    {
        var path = Path.GetFullPath(directory);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("ntdll.dll")]
    private static extern int RtlGetLastNtStatus();

    internal bool IsWatchingDirectory(string directory)
    {
        lock (_watcherLock)
        {
            return _directoryWatchers.ContainsKey(Path.GetFullPath(directory));
        }
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
