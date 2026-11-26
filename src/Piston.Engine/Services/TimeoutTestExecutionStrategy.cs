// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Orchestration;

namespace Piston.Engine.Services;

/// <summary>
/// Cuts a project's run short when one of its tests runs longer than the test timeout, or when it is stopped on
/// request, leaving the other projects running. Before cancelling it captures hang diagnostics, when configured.
/// The result keeps the tests that finished: the hung test fails with a timed-out (or stopped) message and the tests
/// that had not finished are <see cref="TestStatus.NotRun"/>. Only runners that report tests as they start
/// (<see cref="TestStatus.Running"/>) can time a test; any run can be stopped.
/// </summary>
public sealed class TimeoutTestExecutionStrategy : ITestExecutionStrategy
{
    private readonly ITestExecutionStrategy _inner;
    private readonly Func<TimeSpan?> _timeoutProvider;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<Execution> _executions = [];

    /// <summary>Uses a fixed test timeout.</summary>
    public TimeoutTestExecutionStrategy(ITestExecutionStrategy inner, TimeSpan timeout, TimeProvider? timeProvider = null)
        : this(inner, FixedTimeout(timeout), timeProvider)
    {
    }

    /// <summary>Reads the test timeout when each project's run starts; null turns the timeout off for that run.</summary>
    public TimeoutTestExecutionStrategy(ITestExecutionStrategy inner, Func<TimeSpan?> timeoutProvider, TimeProvider? timeProvider = null)
    {
        _inner = inner;
        _timeoutProvider = timeoutProvider;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Captures stacks and dumps of a hung test's processes before its run is cancelled.</summary>
    internal IHangDiagnostics? Diagnostics { get; init; }

    /// <summary>How long hang diagnostics may take before the run is cancelled without them.</summary>
    internal TimeSpan DiagnosticsTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public bool CanExecute(string projectPath) => _inner.CanExecute(projectPath);

    public Task<IReadOnlyList<TestResult>> DiscoverAsync(
        string projectPath,
        string? filter,
        Action<IReadOnlyList<TestResult>>? onPartial,
        CancellationToken ct) =>
        _inner.DiscoverAsync(projectPath, filter, onPartial, ct);

    /// <summary>
    /// Stops the runs of <paramref name="projectPath"/>, or when null, the run with the longest-running test (else the
    /// oldest run). The hung test is reported as stopped; this returns once the stop has been requested.
    /// </summary>
    public StopTestResult Stop(string? projectPath = null)
    {
        List<Execution> targets;
        lock (_gate)
        {
            if (projectPath is not null)
            {
                targets = [.. _executions.Where(e => PathsEqual(e.ProjectPath, projectPath))];
            }
            else
            {
                var longest = _executions
                    .Select(e => (Execution: e, Test: e.Longest()))
                    .Where(x => x.Test is not null)
                    .OrderByDescending(x => x.Test!.Value.Elapsed)
                    .Select(x => x.Execution)
                    .FirstOrDefault();
                targets = longest is not null ? [longest] : [.. _executions.Take(1)];
            }
        }

        string? stoppedProject = null;
        string? stoppedTest = null;
        foreach (var execution in targets)
        {
            var test = execution.Longest();
            if (Trigger(execution, test?.Test, test?.Elapsed, stopped: true))
            {
                stoppedProject ??= execution.ProjectPath;
                stoppedTest ??= test?.Test;
            }
        }

        return new StopTestResult(stoppedProject is not null, stoppedProject, stoppedTest);
    }

    public async Task<ProjectTestResult> ExecuteAsync(
        ProjectTestRequest request,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct)
    {
        TimeSpan? timeout;
        try
        {
            timeout = _timeoutProvider() is { } value && value > TimeSpan.Zero ? value : null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Instance?.Write("Timeout", $"Could not read the test timeout: {ex.Message}");
            timeout = null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var processes = OwnedProcessScope.Begin();
        var execution = new Execution(request.ProjectPath, new RunningTestClock(_time), cts, processes, ct);

        void Progress(IReadOnlyList<TestSuite> suites)
        {
            execution.Observe(suites);
            onProgress?.Invoke(suites);
        }

        void Check()
        {
            if (execution.Longest() is { } longest && longest.Elapsed >= timeout)
            {
                _ = Trigger(execution, longest.Test, longest.Elapsed, stopped: false, timeout);
            }
        }

        lock (_gate)
        {
            _executions.Add(execution);
        }

        ProjectTestResult result;
        var interrupted = false;
        try
        {
            var interval = timeout is { } t
                ? TimeSpan.FromMilliseconds(Math.Clamp(t.TotalMilliseconds / 4, 10, 1000))
                : Timeout.InfiniteTimeSpan;
            await using (timeout is null ? null : _time.CreateTimer(_ => Check(), null, interval, interval))
            {
                try
                {
                    result = await _inner.ExecuteAsync(request, Progress, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && execution.Hit is not null)
                {
                    result = new ProjectTestResult(request.ProjectPath, [], null, [], Crashed: false);
                }
                finally
                {
                    // Runners may return what finished (an earlier target framework, say) instead of throwing.
                    interrupted = execution.MarkCompleted();
                }
            }

            if (execution.Capture is { } capture)
            {
                try
                {
                    await capture.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _ = _executions.Remove(execution);
            }
        }

        // A run that finished before the cancellation landed keeps its result.
        if (execution.Hit is not { } hit || ct.IsCancellationRequested || !interrupted)
        {
            return result;
        }

        // Distinct tests may share a result key (MTP keys them by node UID), so every finished entry is kept.
        var finished = result.Suites.SelectMany(s => s.Tests)
            .Where(t => t.Status != TestStatus.Running)
            .ToLookup(t => t.ResultKey);

        var message = hit.Stopped
            ? $"Stopped after {Durations.Format(hit.Elapsed ?? TimeSpan.Zero)}: the run was stopped on request."
            : $"Timed out after {Durations.Format(hit.Elapsed ?? TimeSpan.Zero)}: the test ran longer than the test timeout ({Durations.Format(hit.Limit ?? TimeSpan.Zero)}).";
        var diagnostics = execution.DiagnosticsResult;
        var latest = execution.Latest;
        var reported = latest.SelectMany(s => s.Tests).Select(t => t.ResultKey).ToHashSet();
        var unreported = finished.Where(g => !reported.Contains(g.Key)).SelectMany(g => g).ToList();
        var merged = new HashSet<string>();
        var suites = latest
            .Select(s => s with
            {
                Tests = s.Tests
                    .SelectMany(t => finished.Contains(t.ResultKey)
                        ? (merged.Add(t.ResultKey) ? finished[t.ResultKey] : [])
                        : [Interrupted(t)])
                    .ToList(),
            })
            .ToList();
        if (unreported.Count > 0)
        {
            suites.Add(new TestSuite(
                TestProjectNames.SuiteName(request.ProjectPath),
                unreported,
                DateTimeOffset.UtcNow,
                TimeSpan.FromTicks(unreported.Sum(t => t.Duration.Ticks))));
        }

        // Partial coverage is not measured, but the runner left its results directories for cleanup.
        var temporaryDirectories = result.TemporaryDirectories
            .Concat(result.CoverageReportPaths
                .Select(PistonOrchestrator.CoverageResultsDirectory)
                .OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProjectTestResult(request.ProjectPath, suites, null, [], Crashed: false)
        {
            Log = result.Log,
            TimedOut = new TestTimeout(hit.Test, hit.Stopped ? null : hit.Limit, hit.Stopped),
            TemporaryDirectories = temporaryDirectories,
        };

        TestResult Interrupted(TestResult t) => t.ResultKey == hit.Test
            ? t with
            {
                Status = TestStatus.Failed,
                Duration = hit.Elapsed ?? t.Duration,
                ErrorMessage = message,
                StackTrace = diagnostics?.Stacks ?? t.StackTrace,
                Attachments = diagnostics is null ? t.Attachments : [.. t.Attachments, .. diagnostics.Attachments],
            }
            : t.Status == TestStatus.Running ? t with { Status = TestStatus.NotRun } : t;
    }

    private bool Trigger(Execution execution, string? test, TimeSpan? elapsed, bool stopped, TimeSpan? limit = null)
    {
        if (!execution.TryHit(new Hit(test, elapsed, limit, stopped)))
        {
            return false;
        }

        var project = Path.GetFileName(execution.ProjectPath);
        DiagnosticLog.Instance?.Write("Timeout", stopped
            ? $"Stopping {project}{(test is null ? "" : $" while {test} was running")}"
            : $"{test} ran longer than the test timeout ({Durations.Format(limit ?? TimeSpan.Zero)}); cancelling {project}");

        if (Diagnostics is not { } diagnostics || test is null || execution.Completed)
        {
            execution.Cancel();
            return true;
        }

        var testResult = execution.Latest.SelectMany(s => s.Tests).FirstOrDefault(t => t.ResultKey == test);
        execution.Capture = Task.Run(async () =>
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(execution.CallerToken);
                timeoutCts.CancelAfter(DiagnosticsTimeout);

                // Stack reads and dumps are synchronous and may not observe the token, so the wait itself is bounded
                // to release the run; cancelling it kills the processes a stalled capture is attached to.
                execution.DiagnosticsResult = await diagnostics.CaptureAsync(
                        new HangCapture(execution.ProjectPath, test, testResult?.Method, execution.Processes.Processes),
                        timeoutCts.Token)
                    .WaitAsync(DiagnosticsTimeout, execution.CallerToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Instance?.Write("Timeout", $"Hang diagnostics failed for {test}: {ex.Message}");
            }
            finally
            {
                execution.Cancel();
            }
        });
        return true;
    }

    private static Func<TimeSpan?> FixedTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return () => timeout;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record Hit(string? Test, TimeSpan? Elapsed, TimeSpan? Limit, bool Stopped);

    private sealed class Execution(
        string projectPath,
        RunningTestClock clock,
        CancellationTokenSource cts,
        OwnedProcessScope processes,
        CancellationToken callerToken)
    {
        private readonly Lock _gate = new();
        private IReadOnlyList<TestSuite> _latest = [];
        private Hit? _hit;
        private bool _completed;
        private bool _cancelledBeforeCompletion;

        public string ProjectPath => projectPath;

        public OwnedProcessScope Processes => processes;

        public CancellationToken CallerToken => callerToken;

        public Task? Capture { get; set; }

        public HangDiagnosticsResult? DiagnosticsResult { get; set; }

        public IReadOnlyList<TestSuite> Latest
        {
            get
            {
                lock (_gate)
                {
                    return _latest;
                }
            }
        }

        public Hit? Hit
        {
            get
            {
                lock (_gate)
                {
                    return _hit;
                }
            }
        }

        public bool Completed
        {
            get
            {
                lock (_gate)
                {
                    return _completed;
                }
            }
        }

        public void Observe(IReadOnlyList<TestSuite> suites)
        {
            lock (_gate)
            {
                _latest = suites;
                clock.Observe(projectPath, suites);
            }
        }

        public (string Test, TimeSpan Elapsed)? Longest()
        {
            lock (_gate)
            {
                return clock.Longest() is { } longest ? (longest.Test, longest.Elapsed) : null;
            }
        }

        public bool TryHit(Hit hit)
        {
            lock (_gate)
            {
                if (_hit is not null)
                {
                    return false;
                }

                _hit = hit;
                return true;
            }
        }

        /// <summary>Marks the run completed and returns whether it was cancelled here before it completed.</summary>
        public bool MarkCompleted()
        {
            lock (_gate)
            {
                _completed = true;
                return _cancelledBeforeCompletion;
            }
        }

        public void Cancel()
        {
            lock (_gate)
            {
                _cancelledBeforeCompletion |= !_completed;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

/// <summary>The outcome of <see cref="TimeoutTestExecutionStrategy.Stop"/>.</summary>
/// <param name="Stopped">Whether a run was stopped.</param>
/// <param name="ProjectPath">The project whose run was stopped.</param>
/// <param name="TestName">The test that was running, if any.</param>
public sealed record StopTestResult(bool Stopped, string? ProjectPath = null, string? TestName = null);
