// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
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

    // Runs cancelled by pausing that have not finished yet; a quick pause, resume and pause can leave two.
    private readonly ConcurrentDictionary<CancellationTokenSource, byte> _pauseCancelledRuns = new();

    // The test phase of the newest run, so pausing can tell which test projects it had still to run; guarded by
    // _lifecycleLock.
    private ActiveTestPhase? _activeTestPhase;

    // What resuming continues: the test projects a run cancelled by pausing in its test phase had still to run;
    // guarded by _lifecycleLock.
    private PausedContinuation? _pausedContinuation;

    /// <summary>
    /// The rest of a run that pausing cancelled during its test phase: the test projects it had not finished, run
    /// with its trigger and effective filter. <see cref="ChangeGeneration"/>, <see cref="TestedThroughGeneration"/>
    /// and <see cref="FullScope"/> describe the original run, so a continuation covers what that run would have.
    /// </summary>
    private sealed record PausedContinuation(
        TestRunTrigger Trigger,
        IReadOnlyList<string> RemainingTestProjects,
        string? EffectiveFilter,
        string? UserFilter,
        long ChangeGeneration,
        long? TestedThroughGeneration,
        bool FullScope);

    // A run's test phase; Settled is written by the run and read by Pause().
    private sealed class ActiveTestPhase(CancellationTokenSource cts, PausedContinuation run)
    {
        private readonly ConcurrentDictionary<string, byte> _settled = new(StringComparer.Ordinal);

        public CancellationTokenSource Cts => cts;

        public PausedContinuation Run => run;

        public void Settle(string projectPath) => _settled[projectPath] = 0;

        public PausedContinuation Remaining() =>
            run with { RemainingTestProjects = [.. run.RemainingTestProjects.Where(p => !_settled.ContainsKey(p))] };
    }

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
    /// stops. The active run is cancelled and its changed files are queued again; when it was running tests, the test
    /// projects it had not finished are kept for <see cref="Resume"/> to continue with. Requested runs still execute.
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
                _pausedContinuation = _activeTestPhase is { } phase && ReferenceEquals(phase.Cts, cts)
                    ? phase.Remaining()
                    : null;
                _pauseCancelledRuns[cts] = 0;
                cts.Cancel();
            }

            PublishPauseState();
        }

        DiagnosticLog.Instance?.Write("Orchestrator", "Processing paused.");
        _state.NotifyChanged();
    }

    /// <summary>
    /// Resumes automatic processing. When pausing interrupted a run's tests and nothing changed since that run started,
    /// continues it with the test projects it had not finished. Otherwise reruns a startup run that pausing cancelled,
    /// or, when files changed while paused, starts one run for all of them, or else restarts the idle impact backfill.
    /// </summary>
    public void Resume()
    {
        FileChangeBatch? batch = null;
        var rerunStartup = false;
        PausedContinuation? continuation = null;
        RegisteredRun? registration = null;
        var solutionPath = _solutionPath;
        lock (_lifecycleLock)
        {
            if (!_paused)
            {
                return;
            }

            _paused = false;
            if (TakeValidContinuationLocked() is { } valid)
            {
                // Every change queued while paused was made before the interrupted run started, so it covers them.
                continuation = valid;
                if (_pausedChanges.Count > 0)
                {
                    // Not analysed again, but kept as the run's changes so pausing the continuation queues them again.
                    var changes = _pausedChanges.Values.Select(c => c.Change).OrderBy(c => c.Timestamp).ToList();
                    batch = new FileChangeBatch(changes, changes[^1].Timestamp);
                }

                _pausedChanges.Clear();
            }
            else if (_pausedStartupRun)
            {
                // Kept until DropPausedChangesCoveredBy sees a completed full-scope run, so a failed or filtered
                // rerun is retried by the next resume.
                rerunStartup = true;
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
            if (continuation is not null && solutionPath is not null && !_disposed)
            {
                registration = RegisterRunLocked(batch, continuation.Trigger);
                _activeTestPhase = new ActiveTestPhase(registration.Cts, continuation);
            }
            else if ((rerunStartup || batch is not null) && solutionPath is not null && !_disposed)
            {
                registration = RegisterRunLocked(batch, rerunStartup ? TestRunTrigger.Startup : TestRunTrigger.FileChange);
            }

            PublishPauseState();
        }

        DiagnosticLog.Instance?.Write("Orchestrator",
            continuation is not null ? $"Processing resumed; continuing the interrupted run with {continuation.RemainingTestProjects.Count} test project(s)."
            : rerunStartup ? "Processing resumed; rerunning the startup run."
            : batch is null ? "Processing resumed."
            : $"Processing resumed with {batch.Changes.Count} queued change(s).");
        _state.NotifyChanged();
        if (registration is null)
        {
            return;
        }

        if (continuation is not null)
        {
            var selection = new SelectedRun(continuation.RemainingTestProjects, continuation.EffectiveFilter) { Continues = continuation };
            _ = RunTrackedAsync(solutionPath!, null, registration, selection);
        }
        else
        {
            _ = RunTrackedAsync(solutionPath!, batch, registration, null);
        }
    }

    // The interrupted run to continue, if nothing it depended on changed since it started. A startup run that is
    // still owed is rerun in full unless the interrupted run is that startup run. Caller holds _lifecycleLock.
    private PausedContinuation? TakeValidContinuationLocked()
    {
        var continuation = _pausedContinuation;
        _pausedContinuation = null;
        return continuation is not null
            && Interlocked.Read(ref _fileChangeGeneration) == continuation.ChangeGeneration
            && string.Equals(continuation.UserFilter, NormalizeFilter(_state.TestFilter), StringComparison.Ordinal)
            && (!_pausedStartupRun || continuation.Trigger == TestRunTrigger.Startup)
            ? continuation
            : null;
    }

    private static string? NormalizeFilter(string? filter) => string.IsNullOrWhiteSpace(filter) ? null : filter;

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

        // The interrupted run no longer reflects the sources, so resume runs for the changes instead.
        _pausedContinuation = null;
        PublishPauseState();
        return true;
    }

    // A completed full-scope run, or a completed continuation of one, covered the changes queued before the full-scope
    // run started, any cancelled startup run and any run left to continue.
    private void DropPausedChangesCoveredBy(TestRunRecord run, long changeGenerationAtStart, PausedContinuation? continues)
    {
        if (run.Status != TestRunStatus.Completed)
        {
            return;
        }

        if (continues is { FullScope: true })
        {
            changeGenerationAtStart = continues.ChangeGeneration;
        }
        else if (!run.IsFullScope || run.Trigger is not (TestRunTrigger.Forced or TestRunTrigger.Startup))
        {
            return;
        }

        lock (_lifecycleLock)
        {
            _startupRunPending = false;
            _pausedStartupRun = false;
            _pausedContinuation = null;
            foreach (var path in _pausedChanges.Where(c => c.Value.Generation <= changeGenerationAtStart).Select(c => c.Key).ToList())
            {
                _ = _pausedChanges.Remove(path);
            }

            PublishPauseState();
        }

        // The run's own completion notification went out before the queue changed.
        _state.NotifyChanged();
    }

    private string? PausedReason(CancellationTokenSource cts) =>
        _pauseCancelledRuns.ContainsKey(cts) ? PausedCancelReason : null;

    // Caller holds _lifecycleLock.
    private void PublishPauseState()
    {
        _state.IsPaused = _paused;
        _state.PausedChangeCount = _pausedChanges.Count;
        _state.HasInterruptedRun = _paused && _pausedContinuation is not null;
    }
}
