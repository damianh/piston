// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Orchestration;

// Idle-time impact backfill: while nothing else runs, re-runs each test class (or test) alone with coverage
// so the impact map knows which class executed which lines. Any run request cancels it.
public sealed partial class PistonOrchestrator
{
    // Guarded by _lifecycleLock.
    private CancellationTokenSource? _backfillCts;
    private Task _backfillTask = Task.CompletedTask;

    // Units whose measurement failed this session, so they are not retried after every run; guarded by _runLock.
    private readonly HashSet<(string Project, string Key)> _backfillFailed = [];

    /// <summary>The granularity measured when idle; null disables the backfill.</summary>
    internal ImpactGranularity? BackfillGranularity { get; set; }

    /// <summary>How long the engine must be idle before the backfill starts.</summary>
    internal TimeSpan BackfillDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The running (or last) backfill pass, for tests.</summary>
    internal Task BackfillTask
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _backfillTask;
            }
        }
    }

    // Caller holds _lifecycleLock.
    private void ScheduleBackfill()
    {
        _backfillCts?.Cancel();
        _backfillCts = null;
        if (BackfillGranularity is not { } granularity || _stopping || _disposed || !_coverageEnabled
            || _coverageStore is not IImpactMapStore || _coverageProcessor is null || _solutionPath is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _backfillCts = cts;
        var previous = _backfillTask;
        _backfillTask = Task.Run(async () =>
        {
            try
            {
                // A cancelled pass may still hold the run lock briefly; it is released before the next one needs it.
                await previous.ConfigureAwait(false);
                await BackfillAsync(granularity, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_lifecycleLock)
                {
                    if (ReferenceEquals(_backfillCts, cts))
                    {
                        _backfillCts = null;
                    }
                }

                cts.Dispose();
            }
        });
    }

    // Caller holds _lifecycleLock.
    private void CancelBackfill()
    {
        _backfillCts?.Cancel();
        _backfillCts = null;
    }

    private async Task BackfillAsync(ImpactGranularity granularity, CancellationToken ct)
    {
        var store = (IImpactMapStore)_coverageStore!;
        var processor = _coverageProcessor!;
        var solutionPath = _solutionPath!;
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
        var granularityName = granularity == ImpactGranularity.Test ? "test" : "class";
        var log = DiagnosticLog.Instance;
        var completed = 0;
        try
        {
            await Task.Delay(BackfillDelay, ct).ConfigureAwait(false);

            // Only results from a run of the current sources describe what the built tests are.
            if (_state.LastRun is not { Status: TestRunStatus.Completed, Freshness: ResultFreshness.Current, StartedAt: { } builtAt })
            {
                return;
            }

            ImpactBackfill.Plan plan;
            await _runLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                plan = ImpactBackfill.Create(
                    _impactAnalyzer.GetAllTestProjectPaths(),
                    _state.TestSuites,
                    project => store.GetUnits(project, granularity),
                    granularity,
                    _backfillFailed);
            }
            finally
            {
                _ = _runLock.Release();
            }

            foreach (var (project, keys) in plan.Obsolete)
            {
                await store.RemoveUnitsAsync(project, granularity, keys).ConfigureAwait(false);
            }

            if (plan.Units.Count == 0)
            {
                return;
            }

            log?.Write("Backfill", $"Measuring {plan.Units.Count} {granularityName} unit(s)");
            foreach (var unit in plan.Units)
            {
                PublishBackfill(new ImpactBackfillProgress(granularityName, completed, plan.Units.Count, unit.Key, unit.ProjectPath));
                await _runLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // The outputs must still be what the last run built and tested.
                    if (_buildBaseline is not { } baseline || Interlocked.Read(ref _fileChangeGeneration) != baseline.ChangeGeneration)
                    {
                        return;
                    }

                    await MeasureUnitAsync(unit, granularity, builtAt.UtcDateTime, store, processor, solutionPath, solutionDir, ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _ = _runLock.Release();
                }

                completed++;
            }

            log?.Write("Backfill", $"Measured {completed} {granularityName} unit(s)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log?.Write("Backfill", $"Cancelled after {completed} unit(s)");
        }
        catch (ObjectDisposedException)
        {
            // The engine was disposed mid-pass.
        }
        catch (Exception ex)
        {
            log?.Write("Backfill", $"Failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            PublishBackfill(null);
        }
    }

    // Caller holds _runLock.
    private async Task MeasureUnitAsync(
        ImpactBackfill.Unit unit,
        ImpactGranularity granularity,
        DateTime builtAtUtc,
        IImpactMapStore store,
        ICoverageProcessor processor,
        string solutionPath,
        string? solutionDir,
        CancellationToken ct)
    {
        TestRunResult result;
        using (var lease = await _admission.AdmitAsync($"backfill-{Guid.NewGuid():N}", _ => { }, null, ct).ConfigureAwait(false))
        {
            result = await _testRunner.RunTestsAsync(
                solutionPath, [unit.ProjectPath], unit.Filter, collectCoverage: true, new TestRunCallbacks(), ct).ConfigureAwait(false);
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var ran = result.Suites.SelectMany(s => s.Tests).Select(t => t.FullyQualifiedName).ToHashSet(StringComparer.Ordinal);
            var tests = unit.Tests.Where(ran.Contains).ToList();
            var crashed = result.RunnerError is not null || result.Projects.Any(p => p.Crashed);

            // A test that did not run would keep the unit outdated, so it is not measured again until restart.
            if (crashed || result.CoverageReportPaths.Count == 0 || tests.Count != unit.Tests.Count)
            {
                _ = _backfillFailed.Add((unit.ProjectPath, unit.Key));
                DiagnosticLog.Instance?.Write("Backfill",
                    $"Skipped {unit.Key}: {(crashed ? "the run failed" : result.CoverageReportPaths.Count == 0 ? "no coverage report" : "not every test ran")}");
                if (crashed || result.CoverageReportPaths.Count == 0 || tests.Count == 0)
                {
                    return;
                }
            }

            await _coverageLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var impact = new CoverageImpactOptions(
                    [new CoverageRunUnit(unit.ProjectPath, unit.Key, result.CoverageReportPaths, tests, Merge: false)],
                    granularity,
                    builtAtUtc);
                await processor.RecordImpactAsync(_coverageStore!.CreateRunId(), impact, store, solutionDir).ConfigureAwait(false);
            }
            finally
            {
                _ = _coverageLock.Release();
            }
        }
        finally
        {
            CleanUpCoverageResultsDirs(result.CoverageReportPaths);
        }
    }

    private void PublishBackfill(ImpactBackfillProgress? progress)
    {
        if (_state.ImpactBackfill == progress)
        {
            return;
        }

        _state.ImpactBackfill = progress;
        _state.NotifyChanged();
    }
}
