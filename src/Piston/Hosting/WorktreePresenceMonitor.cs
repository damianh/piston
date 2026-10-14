// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Hosting;

/// <summary>
/// Detects deletion of the worktree a host serves: fires once when both the solution file and the worktree
/// root are missing on <see cref="RequiredMisses"/> consecutive checks. Requiring both, repeatedly, avoids
/// reacting to a branch switch that briefly removes the solution file.
/// </summary>
public sealed class WorktreePresenceMonitor : IDisposable
{
    public const int RequiredMisses = 2;
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(2500);

    private readonly string _solutionPath;
    private readonly string _worktreeRoot;
    private readonly Action<string> _onDeleted;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryExists;
    private readonly Lock _lock = new();
    private ITimer? _timer;
    private int _misses;
    private bool _fired;

    public WorktreePresenceMonitor(
        string solutionPath,
        string worktreeRoot,
        Action<string> onDeleted,
        TimeSpan? interval = null,
        TimeProvider? timeProvider = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null)
    {
        _solutionPath = solutionPath;
        _worktreeRoot = worktreeRoot;
        _onDeleted = onDeleted;
        _interval = interval ?? DefaultInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _fileExists = fileExists ?? File.Exists;
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    public void Start()
    {
        lock (_lock)
        {
            _timer ??= _timeProvider.CreateTimer(_ => Check(), null, _interval, _interval);
        }
    }

    /// <summary>Runs one check; returns true when this check fired the deletion callback.</summary>
    internal bool Check()
    {
        lock (_lock)
        {
            if (_fired)
            {
                return false;
            }

            var missing = !_fileExists(_solutionPath) && !_directoryExists(_worktreeRoot);
            _misses = missing ? _misses + 1 : 0;
            if (_misses < RequiredMisses)
            {
                return false;
            }

            _fired = true;
            _timer?.Dispose();
            _timer = null;
        }

        _onDeleted(_worktreeRoot);
        return true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
