// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Verification;
using Xunit;

namespace Piston.Engine.Tests.Verification;

public sealed class VerificationPolicyTests
{
    private static readonly SourceFingerprint Source = new("piston-source-v2", "abc", 3);
    private static readonly SourceFingerprint Other = new("piston-source-v2", "def", 3);

    private static TestRunRecord Run(
        TestRunStatus status = TestRunStatus.Completed,
        TestRunScope? scope = TestRunScope.Full,
        ResultFreshness freshness = ResultFreshness.Current,
        SourceFingerprint? after = null,
        string? filter = null,
        TestRunTrigger trigger = TestRunTrigger.Forced,
        bool failing = false,
        bool duplicate = false,
        string runId = "run-1") => new()
        {
            RunId = runId,
            EngineInstanceId = "engine",
            Sequence = 1,
            Trigger = trigger,
            Status = status,
            Scope = scope,
            Freshness = freshness,
            RequestedAt = DateTimeOffset.UtcNow,
            SourceAfter = after ?? Source,
            Context = new TestRunContext("/s.slnx", null, null, filter, filter, null, false, "Debug", null, "rt", "os", "/"),
            Suites =
            [
                new TestSuite("A", [new TestResult("A.T1", "T1", failing ? TestStatus.Failed : TestStatus.Passed,
                    TimeSpan.Zero, null, null, null, null)], DateTimeOffset.UtcNow, TimeSpan.Zero),
            ],
            DuplicateTestIds = duplicate ? ["A.T1"] : [],
        };

    [Fact]
    public void Decide_ReusesNewestCurrentFullRun()
    {
        var run = Run();
        var decision = VerificationPolicy.Decide(Source, null, [run], null, false);
        Assert.Equal(VerificationAction.Reuse, decision.Action);
        Assert.Same(run, decision.Run);
    }

    [Fact]
    public void Decide_ReusesBuildFailedForSameSource()
    {
        var decision = VerificationPolicy.Decide(Source, null, [Run(TestRunStatus.BuildFailed)], null, false);
        Assert.Equal(VerificationAction.Reuse, decision.Action);
    }

    public static TheoryData<TestRunRecord> NonReusable => new()
    {
        Run(scope: TestRunScope.Selective),
        Run(filter: "Category=Fast"),
        Run(freshness: ResultFreshness.Stale),
        Run(after: Other),
        Run(TestRunStatus.Cancelled),
        Run(TestRunStatus.Incomplete),
        Run(TestRunStatus.Faulted),
        Run(duplicate: true),
    };

    [Theory]
    [MemberData(nameof(NonReusable))]
    public void Decide_RequestsForcedRunWhenLatestDoesNotQualify(TestRunRecord run) =>
        Assert.Equal(VerificationAction.RequestForced,
            VerificationPolicy.Decide(Source, null, [run], null, false).Action);

    [Fact]
    public void Decide_NewerFailureForSameSourceIsNotHiddenByOlderPass()
    {
        var newerIncomplete = Run(TestRunStatus.Incomplete, runId: "run-2");
        var decision = VerificationPolicy.Decide(Source, null, [newerIncomplete, Run()], null, false);
        Assert.Equal(VerificationAction.RequestForced, decision.Action);
    }

    [Fact]
    public void Decide_PendingChangesOrUnknownSourceForceRun()
    {
        Assert.Equal(VerificationAction.RequestForced, VerificationPolicy.Decide(Source, null, [Run()], null, true).Action);
        Assert.Equal(VerificationAction.RequestForced, VerificationPolicy.Decide(null, null, [Run()], null, false).Action);
    }

    [Fact]
    public void Decide_UserFilterIsFinalNotFullScope()
    {
        var decision = VerificationPolicy.Decide(Source, null, [Run()], "Category=Fast", false);
        Assert.Equal(VerificationAction.Final, decision.Action);
        Assert.Equal(VerificationVerdict.NotFullScope, decision.Verdict);
    }

    [Fact]
    public void Decide_AwaitsActiveFullRunButSupersedesSelectiveWatcherRun()
    {
        var full = Run(TestRunStatus.Running, scope: null, trigger: TestRunTrigger.Forced);
        Assert.Equal(VerificationAction.AwaitActive, VerificationPolicy.Decide(Source, full, [], null, false).Action);

        var watcherFull = Run(TestRunStatus.Running, trigger: TestRunTrigger.FileChange);
        Assert.Equal(VerificationAction.AwaitActive, VerificationPolicy.Decide(Source, watcherFull, [], null, false).Action);

        var watcherUnknown = Run(TestRunStatus.Queued, scope: null, trigger: TestRunTrigger.FileChange);
        Assert.Equal(VerificationAction.RequestForced, VerificationPolicy.Decide(Source, watcherUnknown, [], null, false).Action);

        var selective = Run(TestRunStatus.Running, scope: TestRunScope.Selective, trigger: TestRunTrigger.FileChange);
        Assert.Equal(VerificationAction.RequestForced, VerificationPolicy.Decide(Source, selective, [], null, false).Action);
    }

    [Theory]
    [InlineData(TestRunStatus.Completed, false, VerificationVerdict.Passed, false)]
    [InlineData(TestRunStatus.Completed, true, VerificationVerdict.TestsFailed, false)]
    [InlineData(TestRunStatus.BuildFailed, false, VerificationVerdict.BuildFailed, false)]
    [InlineData(TestRunStatus.Incomplete, false, VerificationVerdict.Incomplete, false)]
    [InlineData(TestRunStatus.Cancelled, false, VerificationVerdict.Cancelled, true)]
    [InlineData(TestRunStatus.Faulted, false, VerificationVerdict.Faulted, false)]
    public void Evaluate_MapsTerminalStatus(TestRunStatus status, bool failing, VerificationVerdict verdict, bool retryable)
    {
        var evaluation = VerificationPolicy.Evaluate(Run(status, failing: failing), Source);
        Assert.Equal(verdict, evaluation.Verdict);
        Assert.Equal(retryable, evaluation.Retryable);
    }

    [Fact]
    public void Evaluate_NeverPassesNonCurrentResults()
    {
        Assert.Equal(VerificationVerdict.Stale, VerificationPolicy.Evaluate(Run(after: Other), Source).Verdict);
        Assert.Equal(VerificationVerdict.Stale, VerificationPolicy.Evaluate(Run(), null).Verdict);
        Assert.Equal(VerificationVerdict.Stale,
            VerificationPolicy.Evaluate(Run(freshness: ResultFreshness.Stale), Source).Verdict);
        Assert.Equal(VerificationVerdict.NotFullScope,
            VerificationPolicy.Evaluate(Run(scope: TestRunScope.Selective), Source).Verdict);
        Assert.Equal(VerificationVerdict.NotFullScope,
            VerificationPolicy.Evaluate(Run(filter: "X"), Source).Verdict);
        Assert.Equal(VerificationVerdict.Incomplete,
            VerificationPolicy.Evaluate(Run(duplicate: true), Source).Verdict);
    }

    [Theory]
    [InlineData(TestStatus.NotRun)]
    [InlineData(TestStatus.Running)]
    public void Evaluate_UnfinishedTestsAreIncompleteEvenInCompletedRun(TestStatus status)
    {
        var run = Run() with
        {
            Suites = [new TestSuite("A",
                [new TestResult("A.T1", "T1", status, TimeSpan.Zero, null, null, null, null)],
                DateTimeOffset.UtcNow, TimeSpan.Zero)],
        };

        Assert.False(run.IsCurrentFullResult);
        Assert.Equal(VerificationVerdict.Incomplete, VerificationPolicy.Evaluate(run, Source).Verdict);
        Assert.Equal(VerificationAction.RequestForced,
            VerificationPolicy.Decide(Source, null, [run], null, false).Action);
    }
}
