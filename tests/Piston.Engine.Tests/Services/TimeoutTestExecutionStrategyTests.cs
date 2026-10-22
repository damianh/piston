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
    public async Task Progress_IsForwarded()
    {
        var inner = new HangingStrategy([Test("T.Slow", TestStatus.Running)]);
        var strategy = new TimeoutTestExecutionStrategy(inner, TimeSpan.FromMilliseconds(50));
        var forwarded = 0;

        _ = await strategy.ExecuteAsync(new ProjectTestRequest("/p/T.csproj", null, false), _ => forwarded++, _ct);

        Assert.True(forwarded > 0);
    }

    private static TestResult Test(string name, TestStatus status) =>
        new(name, name, status, TimeSpan.Zero, null, null, null, null);

    // Reports its tests, then waits for cancellation and returns an empty result, as the real runners do.
    private sealed class HangingStrategy(IReadOnlyList<TestResult> tests) : ITestExecutionStrategy
    {
        public IReadOnlyList<TestResult>? Later { get; init; }
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

            return new ProjectTestResult(request.ProjectPath, [], null, [], Crashed: false);
        }
    }

    private sealed class CompletingStrategy(ProjectTestResult result) : ITestExecutionStrategy
    {
        public ProjectTestResult Result => result;

        public bool CanExecute(string projectPath) => true;

        public Task<ProjectTestResult> ExecuteAsync(
            ProjectTestRequest request, Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            Task.FromResult(result);
    }
}
