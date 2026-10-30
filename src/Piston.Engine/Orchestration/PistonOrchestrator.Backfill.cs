// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Piston.Engine.Coverage;
using Piston.Engine.Execution;
using Piston.Engine.History;
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

    // Units whose measurement failed this session, so they are not retried after every run.
    private readonly ConcurrentDictionary<(string Project, string Key), byte> _backfillFailed = new();

    /// <summary>The granularity measured when idle; null disables the backfill.</summary>
    internal ImpactGranularity? BackfillGranularity { get; set; }

    /// <summary>The maximum number of backfill measurements running at once.</summary>
    internal int BackfillConcurrency { get; set; } = 1;

    /// <summary>The output directory instrumented by a project's collector, or null when it is unknown.</summary>
    internal Func<string, string?> BackfillOutputDirectory { get; set; } = _ => null;

    /// <summary>Clears discoveries from earlier passes before measuring the current test results.</summary>
    internal Action StartBackfillDiscovery { get; set; } = () => { };

    /// <summary>How long a test runs before it is reported as possibly hanging; null turns the warning off.</summary>
    internal TimeSpan? HangThreshold { get; set; }

    /// <summary>Persistent run history that classifies failures; null when history is off.</summary>
    internal RunHistoryStore? RunHistory { get; set; }

    /// <summary>The clock that times running tests.</summary>
    internal TimeProvider RunningTestTime { get; set; } = TimeProvider.System;

    /// <summary>How often running tests are checked against the hang threshold.</summary>
    internal TimeSpan RunningTestCheckInterval { get; set; } = TimeSpan.FromSeconds(1);

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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
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
                    _backfillFailed.Keys.ToHashSet(),
                    solutionDir);
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

            StartBackfillDiscovery();
            log?.Write("Backfill", $"Measuring {plan.Units.Count} {granularityName} unit(s)");
            var outputs = plan.Units.Select(unit => unit.ProjectPath)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(project => project, BackfillOutputIdentity, StringComparer.Ordinal);
            var allOutputsKnown = outputs.Values.All(output => output is not null);
            var roots = new List<string>();
            var rootByOutput = new Dictionary<string, string>(StringComparer.Ordinal);
            if (allOutputsKnown)
            {
                foreach (var output in outputs.Values.OfType<string>().Distinct(StringComparer.Ordinal).OrderBy(path => path.Length))
                {
                    var root = roots.FirstOrDefault(candidate => IsSameOrParentDirectory(candidate, output));
                    if (root is null)
                    {
                        roots.Add(output);
                        root = output;
                    }

                    rootByOutput[output] = root;
                }

                if (BackfillConcurrency > 1 && roots.Count > 1
                    && !CoverageOutputIsolation.CanRunConcurrently(roots, out var reason, ct))
                {
                    allOutputsKnown = false;
                    log?.Write("Backfill", $"Cannot isolate output directories ({reason}); measuring serially");
                }
            }
            else
            {
                log?.Write("Backfill", "A test output directory is unknown; measuring serially");
            }

            var outputGroups = outputs.ToDictionary(
                output => output.Key,
                output => allOutputsKnown ? rootByOutput[output.Value!] : string.Empty,
                StringComparer.Ordinal);
            var groupCount = outputGroups.Values.Distinct(StringComparer.Ordinal).Count();
            var concurrency = Math.Min(Math.Max(1, BackfillConcurrency), groupCount);
            var chunkSize = concurrency == 1
                ? 1
                : (int)Math.Min(plan.Units.Count, (long)concurrency * 2);
            var progressGate = new Lock();
            var activeUnits = new List<ImpactBackfill.Unit>();

            void PublishProgress(
                ImpactBackfill.Unit? started = null, ImpactBackfill.Unit? finished = null, ImpactBackfill.Unit? waiting = null)
            {
                lock (progressGate)
                {
                    if (started is not null)
                    {
                        activeUnits.Add(started);
                    }

                    if (finished is not null)
                    {
                        _ = activeUnits.Remove(finished);
                        completed++;
                    }

                    var currentUnit = activeUnits.Count > 0 ? activeUnits[^1] : waiting ?? finished;
                    if (currentUnit is not null)
                    {
                        PublishBackfill(new ImpactBackfillProgress(
                            granularityName, completed, plan.Units.Count, currentUnit.Key, currentUnit.ProjectPath));
                    }
                }
            }

            foreach (var chunk in plan.Units.Chunk(chunkSize))
            {
                PublishProgress(waiting: chunk[0]);
                await _runLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // The outputs must still be what the last run built and tested.
                    if (_buildBaseline is not { } baseline || Interlocked.Read(ref _fileChangeGeneration) != baseline.ChangeGeneration)
                    {
                        return;
                    }

                    using var lease = await _admission.AdmitAsync($"backfill-{Guid.NewGuid():N}", _ => { }, null, ct)
                        .ConfigureAwait(false);
                    using var stopChunk = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    using var slots = new SemaphoreSlim(concurrency, concurrency);
                    using var resourceState = new SecondaryResourceState(_state);
                    // Collectors may instrument binaries in place, so measurements sharing an output
                    // directory must never overlap. Unknown output locations force a serial pass.
                    var outputGates = chunk.Select(unit => outputGroups[unit.ProjectPath])
                        .Distinct(StringComparer.Ordinal)
                        .ToDictionary(output => output, _ => new SemaphoreSlim(1, 1), StringComparer.Ordinal);

                    async Task MeasureWhenReadyAsync(ImpactBackfill.Unit unit)
                    {
                        var outputGate = outputGates[outputGroups[unit.ProjectPath]];
                        var outputHeld = false;
                        var slotHeld = false;
                        try
                        {
                            await outputGate.WaitAsync(stopChunk.Token).ConfigureAwait(false);
                            outputHeld = true;

                            await slots.WaitAsync(stopChunk.Token).ConfigureAwait(false);
                            slotHeld = true;
                            stopChunk.Token.ThrowIfCancellationRequested();
                            if (Interlocked.Read(ref _fileChangeGeneration) != baseline.ChangeGeneration)
                            {
                                stopChunk.Cancel();
                                return;
                            }

                            PublishProgress(started: unit);
                            await MeasureUnitAsync(
                                unit, granularity, builtAt.UtcDateTime, baseline.ChangeGeneration, store, processor,
                                solutionPath, solutionDir, resourceState, stopChunk.Token).ConfigureAwait(false);
                            if (Interlocked.Read(ref _fileChangeGeneration) != baseline.ChangeGeneration)
                            {
                                stopChunk.Cancel();
                                return;
                            }

                            PublishProgress(finished: unit);
                        }
                        catch
                        {
                            stopChunk.Cancel();
                            throw;
                        }
                        finally
                        {
                            if (slotHeld)
                            {
                                _ = slots.Release();
                            }

                            if (outputHeld)
                            {
                                _ = outputGate.Release();
                            }
                        }
                    }

                    try
                    {
                        await Task.WhenAll(chunk.Select(MeasureWhenReadyAsync)).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        !ct.IsCancellationRequested && Interlocked.Read(ref _fileChangeGeneration) != baseline.ChangeGeneration)
                    {
                        return;
                    }
                    finally
                    {
                        foreach (var gate in outputGates.Values)
                        {
                            gate.Dispose();
                        }
                    }
                }
                finally
                {
                    _ = _runLock.Release();
                }
            }

            var stats = store.GetStats();
            stopwatch.Stop();
            log?.Write(
                "Backfill",
                $"Measured {completed} {granularityName} unit(s) in {stopwatch.Elapsed.TotalSeconds:F1}s; impact map: " +
                $"{stats.Units} units, {stats.FileRows} file rows, {stats.SpanBytes} span bytes, " +
                $"{stats.BranchBytes} branch bytes, {stats.SnapshotBytes} snapshot bytes ({stats.TotalBytes} total)");
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

    private string? BackfillOutputIdentity(string project)
    {
        var directory = BackfillOutputDirectory(project);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        try
        {
            return WorktreeIdentity.CanonicalizePath(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExecutionCoordinationException)
        {
            DiagnosticLog.Instance?.Write("Backfill",
                $"Cannot identify output for {project}; measuring serially: {ex.Message}");
            return null;
        }
    }

    private static bool IsSameOrParentDirectory(string parent, string path) =>
        string.Equals(parent, path, StringComparison.Ordinal)
        || (path.StartsWith(parent, StringComparison.Ordinal)
            && (Path.EndsInDirectorySeparator(parent)
                || path[parent.Length] == Path.DirectorySeparatorChar
                || path[parent.Length] == Path.AltDirectorySeparatorChar));

    // Caller holds _runLock.
    private async Task MeasureUnitAsync(
        ImpactBackfill.Unit unit,
        ImpactGranularity granularity,
        DateTime builtAtUtc,
        long buildGeneration,
        IImpactMapStore store,
        ICoverageProcessor processor,
        string solutionPath,
        string? solutionDir,
        SecondaryResourceState resourceState,
        CancellationToken ct)
    {
        var result = await RunSecondaryTestsAsync(
            solutionPath, [unit.ProjectPath], unit.Filter, collectCoverage: true, onProjectCompleted: null, ct, resourceState,
            reuseDiscovery: true)
            .ConfigureAwait(false);

        try
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Read(ref _fileChangeGeneration) != buildGeneration)
            {
                return;
            }

            var ran = result.Suites.SelectMany(s => s.Tests).Select(t => t.FullyQualifiedName).ToHashSet(StringComparer.Ordinal);
            var tests = unit.Tests.Where(ran.Contains).ToList();
            var crashed = result.RunnerError is not null || result.Projects.Any(p => p.Crashed);

            // A test that did not run would keep the unit outdated, so it is not measured again until restart.
            if (crashed || result.CoverageReportPaths.Count == 0 || tests.Count != unit.Tests.Count)
            {
                _ = _backfillFailed.TryAdd((unit.ProjectPath, unit.Key), 0);
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
                ct.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref _fileChangeGeneration) != buildGeneration)
                {
                    return;
                }

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
