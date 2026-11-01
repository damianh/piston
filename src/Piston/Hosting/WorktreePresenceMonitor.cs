// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Hosting;

/// <summary>
/// Detects deletion of the worktree a host serves: fires once when both the solution file and the worktree
/// root are missing on <see cref="RequiredMisses"/> consecutive checks. Requiring both, repeatedly, avoids
/// reacting to a branch switch that briefly removes the solution file.
/// </summary>
public sealed class WorktreePresenceMonitor(
    string solutionPath,
    string worktreeRoot,
    Action<string> onDeleted,
    TimeSpan? interval = null,
    TimeProvider? timeProvider = null,
    Func<string, bool>? fileExists = null,
    Func<string, bool>? directoryExists = null) : IDisposable
{
    public const int RequiredMisses = 2;
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(2500);
    private readonly TimeSpan _interval = interval ?? DefaultInterval;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly Func<string, bool> _directoryExists = directoryExists ?? Directory.Exists;
    private readonly Lock _lock = new();
    private ITimer? _timer;
    private int _misses;
    private bool _fired;

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

            var missing = !_fileExists(solutionPath) && !_directoryExists(worktreeRoot);
            _misses = missing ? _misses + 1 : 0;
            if (_misses < RequiredMisses)
            {
                return false;
            }

            _fired = true;
            _timer?.Dispose();
            _timer = null;
        }

        onDeleted(worktreeRoot);
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
