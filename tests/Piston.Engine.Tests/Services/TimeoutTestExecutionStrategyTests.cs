// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TimeoutTestExecutionStrategyTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task SlowTest_FailsAsTimedOutAndKeepsFinishedTests()
    {
        var inner = new HangingStrategy([Test("T.Done", TestStatus.Passed), Test("T.Slow", TestStatus.Running)]);
        var strategy = new TimeoutTestExecutionStrategy(inner, TimeSpan.FromMilliseconds(100));

        var result = await strategy.ExecuteAsync(new ProjectTestRequest("/p/T.csproj", null, false), null, _ct);

        Assert.Equal(new TestTimeout("T.Slow", TimeSpan.FromMilliseconds(100)), result.TimedOut);
        Assert.True(inner.Cancelled);
        var tests = Assert.Single(result.Suites).Tests;
        Assert.Equal(TestStatus.Passed, tests.Single(t => t.FullyQualifiedName == "T.Done").Status);
        var slow = tests.Single(t => t.FullyQualifiedName == "T.Slow");
        Assert.Equal(TestStatus.Failed, slow.Status);
        Assert.StartsWith("Timed out after ", slow.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("test timeout (100 ms)", slow.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherRunningTests_BecomeNotRun()
    {
        var inner = new HangingStrategy([Test("T.Slow", TestStatus.Running)])
        {
            Later = [Test("T.Slow", TestStatus.Running), Test("T.Other", TestStatus.Running)],
        };
        var strategy = new TimeoutTestExecutionStrategy(inner, TimeSpan.FromMilliseconds(100));

        var result = await strategy.ExecuteAsync(new ProjectTestRequest("/p/T.csproj", null, false), null, _ct);

        Assert.Equal("T.Slow", result.TimedOut?.Test);
        var tests = Assert.Single(result.Suites).Tests;
        Assert.Equal(TestStatus.NotRun, tests.Single(t => t.FullyQualifiedName == "T.Other").Status);
    }

    [Fact]
    public async Task RunThatFinishesInTime_IsUnchanged()
    {
        var suite = new TestSuite("T", [Test("T.Fast", TestStatus.Passed)], DateTimeOffset.UtcNow, TimeSpan.Zero);
        var inner = new CompletingStrategy(new ProjectTestResult("/p/T.csproj", [suite], null, [], Crashed: false));
        var strategy = new TimeoutTestExecutionStrategy(inner, TimeSpan.FromSeconds(30));

        var result = await strategy.ExecuteAsync(new ProjectTestRequest("/p/T.csproj", null, false), null, _ct);

        Assert.Same(inner.Result, result);
    }

    [Fact]
    public async Task CallerCancellation_IsNotATimeout()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var inner = new HangingStrategy([Test("T.Slow", TestStatus.Running)]);
        var strategy = new TimeoutTestExecutionStrategy(inner, TimeSpan.FromSeconds(30));
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var result = await strategy.ExecuteAsync(new ProjectTestRequest("/p/T.csproj", null, false), null, cts.Token);

        Assert.Null(result.TimedOut);
        Assert.Empty(result.Suites);
    }

    [Fact]
    public async Task WaitsForTimerCallbacksBeforeDisposingTheCancellationSource()
    {
        var suite = new TestSuite("T", [Test("T.Fast", TestStatus.Passed)], DateTimeOffset.UtcNow, TimeSpan.Zero);
        var inner = new CompletingStrategy(new ProjectTestResult("/p/T.csproj", [suite], null, [], Crashed: false),
            reportRunning: true);
        var time = new DeferredTimerTimeProvider();
        var strategy = new TimeoutTestExecutionStrategy(inner, TimeSpan.FromMilliseconds(100), time);

        var execution = strategy.ExecuteAsync(new ProjectTestRequest("/p/T.csproj", null, false), null, _ct);
        try
        {
            Assert.True(time.Timer!.AsyncDisposalStarted);
            Assert.False(execution.IsCompleted);
        }
        finally
        {
            _ = time.Release.TrySetResult();
        }

        Assert.Same(inner.Result, await execution.WaitAsync(TimeSpan.FromSeconds(10), _ct));
        Assert.True(inner.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Progress_IsForwarded()
    {
        var inner = new HangingStrategy([Test("T.Slow", TestStatus.Running)]);
        var strategy = new TimeoutTestExecutionStrategy(inner, TimeSpan.FromMilliseconds(50));
        var forwarded = 0;

        _ = await strategy.ExecuteAsync(new ProjectTestRequest("/p/T.csproj", null, false), _ => forwarded++, _ct);

        Assert.True(forwarded > 0);
    }

    [Fact]
    public async Task NoTimeout_LetsTheRunGoOnUntilStopped()
    {
        var inner = new HangingStrategy([Test("T.Done", TestStatus.Passed), Test("T.Slow", TestStatus.Running)]);
        var strategy = new TimeoutTestExecutionStrategy(inner, () => null);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = strategy.ExecuteAsync(
            new ProjectTestRequest("/p/T.csproj", null, false), _ => reported.TrySetResult(), _ct);
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        await Task.Delay(200, _ct);
        Assert.False(execution.IsCompleted);

        var stop = strategy.Stop();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(new StopTestResult(true, "/p/T.csproj", "T.Slow"), stop);
        Assert.Equal(new TestTimeout("T.Slow", null, Stopped: true), result.TimedOut);
        var tests = Assert.Single(result.Suites).Tests;
        Assert.Equal(TestStatus.Passed, tests.Single(t => t.FullyQualifiedName == "T.Done").Status);
        var slow = tests.Single(t => t.FullyQualifiedName == "T.Slow");
        Assert.Equal(TestStatus.Failed, slow.Status);
        Assert.StartsWith("Stopped after ", slow.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_TargetsTheRequestedProjectOnly()
    {
        var inner = new HangingStrategy([Test("T.Slow", TestStatus.Running)]);
        var strategy = new TimeoutTestExecutionStrategy(inner, () => null);
        var started = new CountdownEvent(2);
        var first = strategy.ExecuteAsync(new ProjectTestRequest(Rooted("A.csproj"), null, false), _ => Signal(started), _ct);
        var second = strategy.ExecuteAsync(new ProjectTestRequest(Rooted("B.csproj"), null, false), _ => Signal(started), _ct);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), _ct));

        Assert.False(strategy.Stop(Rooted("C.csproj")).Stopped);
        var stop = strategy.Stop(Rooted("B.csproj"));

        Assert.Equal(Rooted("B.csproj"), stop.ProjectPath);
        Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(10), _ct)).TimedOut!.Stopped);
        Assert.False(first.IsCompleted);
        _ = strategy.Stop();
        _ = await first.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        static void Signal(CountdownEvent e)
        {
            if (!e.IsSet)
            {
                _ = e.Signal();
            }
        }
    }

    [Fact]
    public void Stop_WithoutRunningProjects_StopsNothing()
    {
        var strategy = new TimeoutTestExecutionStrategy(new HangingStrategy([]), () => null);

        Assert.Equal(new StopTestResult(false), strategy.Stop());
    }

    [Fact]
    public async Task Timeout_CapturesDiagnosticsBeforeCancelling()
    {
        var inner = new HangingStrategy([Test("T.Slow", TestStatus.Running) with { Method = "T.Slow()" }]);
        var attachment = new TestAttachment("/tmp/dump.dmp", "hang-1.dmp", null);
        var diagnostics = new FakeDiagnostics(inner, new HangDiagnosticsResult("   at T.Slow()", [attachment]));
        var strategy = new TimeoutTestExecutionStrategy(inner, TimeSpan.FromMilliseconds(100)) { Diagnostics = diagnostics };

        var result = await strategy.ExecuteAsync(new ProjectTestRequest("/p/T.csproj", null, false), null, _ct);

        var capture = Assert.Single(diagnostics.Captures);
        Assert.Equal(("/p/T.csproj", "T.Slow", "T.Slow()"), (capture.ProjectPath, capture.Test, capture.Method));
        Assert.False(diagnostics.CancelledBeforeCapture);
        var slow = Assert.Single(Assert.Single(result.Suites).Tests);
        Assert.Equal(TestStatus.Failed, slow.Status);
        Assert.Equal("   at T.Slow()", slow.StackTrace);
        Assert.Contains(attachment, slow.Attachments);
    }

    [Fact]
    public async Task FailingDiagnostics_StillCancelTheRun()
    {
        var inner = new HangingStrategy([Test("T.Slow", TestStatus.Running)]);
        var strategy = new TimeoutTestExecutionStrategy(inner, TimeSpan.FromMilliseconds(100))
        {
            Diagnostics = new FakeDiagnostics(inner, null, new InvalidOperationException("boom")),
        };

        var result = await strategy.ExecuteAsync(new ProjectTestRequest("/p/T.csproj", null, false), null, _ct);

        Assert.True(inner.Cancelled);
        Assert.Equal("T.Slow", result.TimedOut?.Test);
    }

    [Fact]
    public async Task StalledDiagnostics_StillCancelTheRun()
    {
        var inner = new HangingStrategy([Test("T.Slow", TestStatus.Running)]);
        var strategy = new TimeoutTestExecutionStrategy(inner, TimeSpan.FromMilliseconds(100))
        {
            Diagnostics = new FakeDiagnostics(inner, null) { Stall = true },
            DiagnosticsTimeout = TimeSpan.FromMilliseconds(200),
        };

        var result = await strategy.ExecuteAsync(new ProjectTestRequest("/p/T.csproj", null, false), null, _ct);

        Assert.True(inner.Cancelled);
        Assert.Equal("T.Slow", result.TimedOut?.Test);
    }

    [Fact]
    public async Task RunnerReturningEarlierResultsOnCancellation_StillTimesOut()
    {
        // A multi-target project returns the frameworks that finished when a later one is cancelled.
        var inner = new HangingStrategy([Test("T.Slow", TestStatus.Running)])
        {
            Finished = [Test("T.Earlier", TestStatus.Passed)],
        };
        var strategy = new TimeoutTestExecutionStrategy(inner, TimeSpan.FromMilliseconds(100));

        var result = await strategy.ExecuteAsync(new ProjectTestRequest("/p/T.csproj", null, false), null, _ct);

        Assert.Equal("T.Slow", result.TimedOut?.Test);
        var tests = result.Suites.SelectMany(s => s.Tests).ToList();
        Assert.Equal(TestStatus.Passed, tests.Single(t => t.FullyQualifiedName == "T.Earlier").Status);
        Assert.Equal(TestStatus.Failed, tests.Single(t => t.FullyQualifiedName == "T.Slow").Status);
    }

    private static string Rooted(string file) => Path.Combine(Path.GetTempPath(), "piston-stop", file);

    private sealed class FakeDiagnostics(HangingStrategy inner, HangDiagnosticsResult? result, Exception? error = null)
        : IHangDiagnostics
    {
        public List<HangCapture> Captures { get; } = [];
        public bool CancelledBeforeCapture { get; private set; }
        public bool Stall { get; init; }

        public Task<HangDiagnosticsResult?> CaptureAsync(HangCapture capture, CancellationToken ct)
        {
            Captures.Add(capture);
            CancelledBeforeCapture = inner.Cancelled;
            if (Stall)
            {
                // Like a synchronous stack read that never observes the token.
                return new TaskCompletionSource<HangDiagnosticsResult?>().Task;
            }

            return error is null ? Task.FromResult(result) : Task.FromException<HangDiagnosticsResult?>(error);
        }
    }

    private static TestResult Test(string name, TestStatus status) =>
        new(name, name, status, TimeSpan.Zero, null, null, null, null);

    // Reports its tests, then waits for cancellation and returns an empty result, as the real runners do.
    private sealed class HangingStrategy(IReadOnlyList<TestResult> tests) : ITestExecutionStrategy
    {
        public IReadOnlyList<TestResult>? Later { get; init; }
        public IReadOnlyList<TestResult> Finished { get; init; } = [];
        public bool Cancelled { get; private set; }

        public bool CanExecute(string projectPath) => true;

        public async Task<ProjectTestResult> ExecuteAsync(
            ProjectTestRequest request, Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct)
        {
            onProgress?.Invoke([new TestSuite("T", tests, DateTimeOffset.UtcNow, TimeSpan.Zero)]);
            if (Later is { } later)
            {
                await Task.Delay(20, CancellationToken.None);
                onProgress?.Invoke([new TestSuite("T", later, DateTimeOffset.UtcNow, TimeSpan.Zero)]);
            }

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
            }

            IReadOnlyList<TestSuite> finished = Finished.Count == 0
                ? []
                : [new TestSuite("T", Finished, DateTimeOffset.UtcNow, TimeSpan.Zero)];
            return new ProjectTestResult(request.ProjectPath, finished, null, [], Crashed: false);
        }
    }

    private sealed class CompletingStrategy(ProjectTestResult result, bool reportRunning = false) : ITestExecutionStrategy
    {
        public ProjectTestResult Result => result;
        public CancellationToken Token { get; private set; }

        public bool CanExecute(string projectPath) => true;

        public Task<ProjectTestResult> ExecuteAsync(
            ProjectTestRequest request, Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct)
        {
            Token = ct;
            if (reportRunning)
            {
                onProgress?.Invoke([.. result.Suites.Select(s => s with
                {
                    Tests = [.. s.Tests.Select(t => t with { Status = TestStatus.Running })],
                })]);
            }

            return Task.FromResult(result);
        }
    }

    private sealed class DeferredTimerTimeProvider : TimeProvider
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DeferredTimer? Timer { get; private set; }
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new DeferredTimer(this, callback, state);

        public sealed class DeferredTimer(DeferredTimerTimeProvider time, TimerCallback callback, object? state) : ITimer
        {
            public bool AsyncDisposalStarted { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }

            public async ValueTask DisposeAsync()
            {
                AsyncDisposalStarted = true;
                await time.Release.Task;
                time._now = time._now.AddSeconds(1);
                callback(state);
            }
        }
    }
}
