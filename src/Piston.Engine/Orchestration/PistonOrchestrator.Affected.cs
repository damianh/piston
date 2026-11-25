// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Orchestration;

public sealed partial class PistonOrchestrator
{
    /// <summary>
    /// Most runs one <see cref="RunAffectedAsync"/> call starts when its runs keep being superseded or finish on a
    /// source that changed while they ran.
    /// </summary>
    internal int MaxAffectedRunAttempts { get; set; } = 3;

    /// <summary>
    /// Tests the changes to <paramref name="filePaths"/> (absolute). Waits for the file watcher and any active run
    /// first, because an edit starts a run of its own. Then the latest run is reused when it already tested those
    /// changes on the present source; otherwise the tests the changes select are run, without superseding another run.
    /// </summary>
    /// <param name="filePaths">The changed files, absolute.</param>
    /// <param name="ct">Stops the wait; a run that was awaited or started continues.</param>
    /// <exception cref="InvalidOperationException">The engine is not ready.</exception>
    internal async Task<AffectedRun> RunAffectedAsync(IReadOnlyList<string> filePaths, CancellationToken ct)
    {
        var solutionPath = _solutionPath ?? throw new InvalidOperationException("The engine has not been started.");
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? Path.GetFullPath(solutionPath);
        var attempts = 0;
        while (true)
        {
            do
            {
                await WaitForWatcherToSettleAsync(ct).ConfigureAwait(false);
                await AwaitPipelinesDrainedAsync(ct).ConfigureAwait(false);
            }
            while (!_stopping && _fileWatcher.MonitoringError is null && _fileWatcher.HasPendingChanges);

            // Checked before the plan's scope: once a run has measured the edited files, they select nothing.
            var plan = PlanAffected(filePaths);
            if (FindRunOfChanges(filePaths, solutionDir) is { } tested)
            {
                return new AffectedRun(plan, tested, Reused: true);
            }

            if (!plan.IsFullRun && plan.AffectedTestProjectPaths.Count == 0)
            {
                return new AffectedRun(plan, null, Reused: false);
            }

            var requested = plan.IsFullRun
                ? TryTriggerRun(solutionPath, null, TestRunTrigger.Forced, selection: null, supersede: false)
                : TryRunPlan(plan, solutionDir);
            if (requested is null)
            {
                // A run started since the wait, normally the watcher's run of the same edit.
                continue;
            }

            var run = await requested.WaitAsync(ct).ConfigureAwait(false);
            if ((run.Status == TestRunStatus.Cancelled || run.Freshness == ResultFreshness.Stale)
                && !_stopping && ++attempts < MaxAffectedRunAttempts)
            {
                // Superseded by the run of a newer change, which may cover these files, or the source changed
                // while it ran, so its results describe older code.
                continue;
            }

            return new AffectedRun(plan, run, Reused: false);
        }
    }

    // Null when a run is already in progress: the plan's run never supersedes it.
    private Task<TestRunRecord>? TryRunPlan(ImpactAnalysisResult plan, string solutionDir) =>
        TryRunSelected(new TestSelection(
            [.. plan.AffectedTestProjectPaths
                .Select(p => TestProjectNames.ProjectId(solutionDir, p))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            plan.NarrowsToTests ? [.. plan.AffectedTestFqns!.Order(StringComparer.Ordinal)] : null));

    /// <summary>
    /// The latest run of this engine when it tested the changes to <paramref name="filePaths"/> on the present
    /// source: it built (or failed to build) that source, no user filter narrowed it, and it ran every test or was
    /// a file-change run that analysed every one of the files.
    /// </summary>
    private TestRunRecord? FindRunOfChanges(IReadOnlyList<string> filePaths, string solutionDir)
    {
        TestRunRecord? latest;
        lock (_runRecordsLock)
        {
            latest = _state.RecentRuns
                .Where(r => string.Equals(r.EngineInstanceId, _engineInstanceId, StringComparison.Ordinal))
                .MaxBy(r => r.Sequence);
        }

        if (latest is not
            {
                Restored: false,
                Status: TestRunStatus.Completed or TestRunStatus.BuildFailed,
                Freshness: ResultFreshness.Current,
                Context.UserFilter: null,
            })
        {
            return null;
        }

        if (!latest.IsFullScope)
        {
            if (latest.Trigger != TestRunTrigger.FileChange)
            {
                return null;
            }

            var analysed = latest.ChangedFiles.Select(Path.GetFullPath).ToHashSet(PathComparer);
            if (!filePaths.All(p => analysed.Contains(Path.GetFullPath(p))))
            {
                return null;
            }
        }

        return ComputeSource(solutionDir) is { } current && latest.SourceAfter == current ? latest : null;
    }
}
