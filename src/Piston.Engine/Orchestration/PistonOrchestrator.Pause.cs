// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Orchestration;

public sealed partial class PistonOrchestrator
{
    private const string PausedCancelReason = "Cancelled because processing was paused.";

    // Guarded by _lifecycleLock.
    private bool _paused;

    // Changes seen while paused, latest per path with the file-change generation that saw it; guarded by _lifecycleLock.
    private readonly Dictionary<string, (FileChangeEvent Change, long Generation)> _pausedChanges = new(StringComparer.Ordinal);

    // The file changes of the newest requested run, so pausing can queue them again; guarded by _lifecycleLock.
    private IReadOnlyList<FileChangeEvent>? _activeRunChanges;

    // A startup run was requested and no full-scope Startup or Forced run has completed since, so some test
    // projects may still be undiscovered; guarded by _lifecycleLock.
    private bool _startupRunPending;

    // Pausing cancelled the startup run, so resuming runs it again; guarded by _lifecycleLock.
    private bool _pausedStartupRun;

    // The run cancelled by the latest pause; guarded by _lifecycleLock.
    private CancellationTokenSource? _pauseCancelledCts;

    /// <summary>Whether automatic processing is paused.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _paused;
            }
        }
    }

    /// <summary>
    /// Pauses automatic processing: file changes are queued instead of starting runs and the idle impact backfill
    /// stops. The active run is cancelled and its changed files are queued again. Requested runs still execute.
    /// </summary>
    public void Pause()
    {
        lock (_lifecycleLock)
        {
            if (_paused || _disposed)
            {
                return;
            }

            _paused = true;
            CancelBackfill();
            if (_cts is { } cts)
            {
                var generation = Interlocked.Read(ref _fileChangeGeneration);
                foreach (var change in _activeRunChanges ?? [])
                {
                    _ = _pausedChanges.TryAdd(change.FilePath, (change, generation));
                }

                _pausedStartupRun |= _startupRunPending;
                _pauseCancelledCts = cts;
                cts.Cancel();
            }

            PublishPauseState();
        }

        DiagnosticLog.Instance?.Write("Orchestrator", "Processing paused.");
        _state.NotifyChanged();
    }

    /// <summary>
    /// Resumes automatic processing. Reruns a startup run that pausing cancelled; otherwise, when files changed while
    /// paused, starts one run for all of them, or else restarts the idle impact backfill.
    /// </summary>
    public void Resume()
    {
        FileChangeBatch? batch = null;
        var rerunStartup = false;
        CancellationTokenSource? cts = null;
        var solutionPath = _solutionPath;
        lock (_lifecycleLock)
        {
            if (!_paused)
            {
                return;
            }

            _paused = false;
            if (_pausedStartupRun)
            {
                rerunStartup = true;
                _pausedStartupRun = false;
                _pausedChanges.Clear();
            }
            else if (_pausedChanges.Count > 0)
            {
                var changes = _pausedChanges.Values.Select(c => c.Change).OrderBy(c => c.Timestamp).ToList();
                _pausedChanges.Clear();
                batch = new FileChangeBatch(changes, changes[^1].Timestamp);
            }
            else if (_activePipelines == 0)
            {
                ScheduleBackfill();
            }

            // Registered under the lock, so a concurrent Pause() cancels and requeues this run, and a newer
            // watcher run supersedes it rather than the other way round.
            if ((rerunStartup || batch is not null) && solutionPath is not null && !_disposed)
            {
                cts = RegisterRunLocked(batch, rerunStartup ? TestRunTrigger.Startup : TestRunTrigger.FileChange);
            }

            PublishPauseState();
        }

        DiagnosticLog.Instance?.Write("Orchestrator",
            rerunStartup ? "Processing resumed; rerunning the startup run." : batch is null ? "Processing resumed." : $"Processing resumed with {batch.Changes.Count} queued change(s).");
        _state.NotifyChanged();
        if (cts is not null)
        {
            _ = RunTrackedAsync(solutionPath!, batch, rerunStartup ? TestRunTrigger.Startup : TestRunTrigger.FileChange, null, cts);
        }
    }

    // Queues a file change while paused; returns false when not paused. Caller holds _lifecycleLock.
    private bool TryQueuePausedChangeLocked(FileChangeBatch batch)
    {
        if (!_paused)
        {
            return false;
        }

        var generation = Interlocked.Read(ref _fileChangeGeneration);
        foreach (var change in batch.Changes)
        {
            _pausedChanges[change.FilePath] = (change, generation);
        }

        PublishPauseState();
        return true;
    }

    // A completed full-scope run covered the changes queued before it started, and any cancelled startup run.
    private void DropPausedChangesCoveredBy(TestRunRecord run, long changeGenerationAtStart)
    {
        if (run.Status != TestRunStatus.Completed
            || !run.IsFullScope
            || run.Trigger is not (TestRunTrigger.Forced or TestRunTrigger.Startup))
        {
            return;
        }

        lock (_lifecycleLock)
        {
            _startupRunPending = false;
            _pausedStartupRun = false;
            foreach (var path in _pausedChanges.Where(c => c.Value.Generation <= changeGenerationAtStart).Select(c => c.Key).ToList())
            {
                _ = _pausedChanges.Remove(path);
            }

            PublishPauseState();
        }

        // The run's own completion notification went out before the queue changed.
        _state.NotifyChanged();
    }

    // Caller holds _lifecycleLock.
    private void ClearPausedChanges()
    {
        _pausedChanges.Clear();
        _pausedStartupRun = false;
        PublishPauseState();
    }

    private string? PausedReason(CancellationTokenSource cts) =>
        ReferenceEquals(Volatile.Read(ref _pauseCancelledCts), cts) ? PausedCancelReason : null;

    // Caller holds _lifecycleLock.
    private void PublishPauseState()
    {
        _state.IsPaused = _paused;
        _state.PausedChangeCount = _pausedChanges.Count;
    }
}
