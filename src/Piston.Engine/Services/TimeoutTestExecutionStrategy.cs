// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Cancels a project's run when one of its tests runs longer than the test timeout, leaving the other
/// projects running. The result keeps the tests that finished: the slow test fails with a timed-out
/// message and the tests that had not finished are <see cref="TestStatus.NotRun"/>. Only runners that
/// report tests as they start (<see cref="TestStatus.Running"/>) can time a test.
/// </summary>
public sealed class TimeoutTestExecutionStrategy : ITestExecutionStrategy
{
    private readonly ITestExecutionStrategy _inner;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly TimeSpan _checkInterval;

    public TimeoutTestExecutionStrategy(ITestExecutionStrategy inner, TimeSpan timeout, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _inner = inner;
        _timeout = timeout;
        _time = timeProvider ?? TimeProvider.System;
        _checkInterval = TimeSpan.FromMilliseconds(Math.Clamp(timeout.TotalMilliseconds / 4, 10, 1000));
    }

    public bool CanExecute(string projectPath) => _inner.CanExecute(projectPath);

    public Task<IReadOnlyList<TestResult>> DiscoverAsync(
        string projectPath,
        string? filter,
        Action<IReadOnlyList<TestResult>>? onPartial,
        CancellationToken ct) =>
        _inner.DiscoverAsync(projectPath, filter, onPartial, ct);

    public async Task<ProjectTestResult> ExecuteAsync(
        ProjectTestRequest request,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gate = new object();
        var clock = new RunningTestClock(_time);
        IReadOnlyList<TestSuite> latest = [];
        (string Test, TimeSpan Elapsed)? timedOut = null;

        void Progress(IReadOnlyList<TestSuite> suites)
        {
            lock (gate)
            {
                latest = suites;
                clock.Observe(request.ProjectPath, suites);
            }

            onProgress?.Invoke(suites);
        }

        void Check()
        {
            lock (gate)
            {
                if (timedOut is not null || clock.Longest() is not { } longest || longest.Elapsed < _timeout)
                {
                    return;
                }

                timedOut = (longest.Test, longest.Elapsed);
            }

            DiagnosticLog.Instance?.Write("Timeout",
                $"{timedOut.Value.Test} ran longer than the test timeout ({Durations.Format(_timeout)}); cancelling {Path.GetFileName(request.ProjectPath)}");
            timeoutCts.Cancel();
        }

        ProjectTestResult result;
        using (_time.CreateTimer(_ => Check(), null, _checkInterval, _checkInterval))
        {
            try
            {
                result = await _inner.ExecuteAsync(request, Progress, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timedOut is not null)
            {
                result = new ProjectTestResult(request.ProjectPath, [], null, [], Crashed: false);
            }
        }

        (string Test, TimeSpan Elapsed)? hit;
        IReadOnlyList<TestSuite> partial;
        lock (gate)
        {
            hit = timedOut;
            partial = latest;
        }

        // A run that finished with results before the cancellation landed keeps them.
        if (hit is not { } slow || ct.IsCancellationRequested || result.Suites.Count > 0)
        {
            return result;
        }

        var message = $"Timed out after {Durations.Format(slow.Elapsed)}: the test ran longer than the test timeout ({Durations.Format(_timeout)}).";
        var suites = partial
            .Select(s => s with
            {
                Tests = s.Tests
                    .Select(t => t.ResultKey == slow.Test
                        ? t with { Status = TestStatus.Failed, Duration = slow.Elapsed, ErrorMessage = message }
                        : t.Status == TestStatus.Running ? t with { Status = TestStatus.NotRun } : t)
                    .ToList(),
            })
            .ToList();

        return new ProjectTestResult(request.ProjectPath, suites, null, [], Crashed: false)
        {
            Log = result.Log,
            TimedOut = new TestTimeout(slow.Test, _timeout),
        };
    }
}
