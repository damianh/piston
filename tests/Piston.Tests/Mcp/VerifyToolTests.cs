// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine;
using Piston.Engine.Models;
using Piston.Engine.Verification;
using Piston.Mcp.Tools;
using Xunit;

namespace Piston.Tests.Mcp;

public sealed class VerifyToolTests
{
    private static readonly SourceFingerprint Source = new("piston-source-v2", new string('a', 64), 3);

    private static TestRunRecord Run(TestRunStatus status = TestRunStatus.Completed, BuildResult? build = null,
        IReadOnlyList<TestResult>? tests = null, TestRunAdmission? admission = null) => new()
        {
            RunId = "run-7",
            EngineInstanceId = "engine",
            Sequence = 7,
            Trigger = TestRunTrigger.Forced,
            Status = status,
            Scope = TestRunScope.Full,
            Freshness = ResultFreshness.Current,
            RequestedAt = DateTimeOffset.UnixEpoch,
            SourceAfter = Source,
            Build = build,
            Admission = admission,
            Suites = tests is null ? [] : [new TestSuite("T", tests, DateTimeOffset.UnixEpoch, TimeSpan.Zero)],
        };

    private static TestResult Test(string fqn, TestStatus status, string? error = null) =>
        new(fqn, fqn, status, TimeSpan.Zero, null, error, null, null);

    private static async Task<string> VerifyAsync(VerificationResult verification, int timeoutSeconds = 600)
    {
        var engine = new StubEngine { NextVerification = verification };
        return await new TestTools(engine, NullMcpCallRecorder.Instance).VerifyAsync(timeoutSeconds, CancellationToken.None);
    }

    [Fact]
    public async Task Passed_ReportsReuseFingerprintCountsAndPointer()
    {
        var run = Run(tests: [Test("A.One", TestStatus.Passed), Test("A.Two", TestStatus.Skipped)]);
        var text = await VerifyAsync(new(VerificationVerdict.Passed, null, VerificationSource.Reused, 1, run, Source));

        Assert.StartsWith("Verdict: Passed", text);
        Assert.Contains("Counts as full verification: yes", text);
        Assert.Contains("Reused: yes (Reused), attempts: 1", text);
        Assert.Contains($"Source: piston-source-v2:{Source.Value} (3 files)", text);
        Assert.Contains("Tests: 2 (Passed: 1, Failed: 0, Skipped: 1)", text);
        Assert.EndsWith("Details: get_test_run(\"run-7\")", text);
    }

    [Fact]
    public async Task TestsFailed_ListsCappedFailures()
    {
        var failures = Enumerable.Range(0, 25)
            .Select(i => Test($"A.Fail{i}", TestStatus.Failed, i == 0 ? new string('x', 1000) + "\nline2" : "boom"))
            .ToList();
        var text = await VerifyAsync(new(VerificationVerdict.TestsFailed, "25 test(s) failed.",
            VerificationSource.Forced, 1, Run(tests: failures), Source));

        Assert.Contains("Counts as full verification: no", text);
        Assert.Contains("Failed tests (25):", text);
        Assert.Contains("  - A.Fail1: boom", text);
        Assert.DoesNotContain("A.Fail24", text);
        Assert.Contains("... 5 more", text);
        Assert.DoesNotContain(new string('x', 500), text);
        Assert.DoesNotContain("\nline2", text);
    }

    [Fact]
    public async Task BuildFailed_ListsBuildErrorsOfThatRun()
    {
        var build = new BuildResult(BuildStatus.Failed, ["/w/A.cs(3,5): error CS0103: The name 'x' does not exist"], [], TimeSpan.Zero);
        var text = await VerifyAsync(new(VerificationVerdict.BuildFailed, "Build failed.",
            VerificationSource.Forced, 1, Run(TestRunStatus.BuildFailed, build), Source));

        Assert.Contains("Verdict: BuildFailed", text);
        Assert.Contains("Build errors (1):", text);
        Assert.Contains("error CS0103", text);
    }

    [Fact]
    public async Task QueuedAdmission_IsReported()
    {
        var admission = new TestRunAdmission(TestRunAdmissionState.Admitted, 2, null, 4, null, "default", 0, 0, 1, 2, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(3));
        var text = await VerifyAsync(new(VerificationVerdict.Passed, null, VerificationSource.Forced, 1,
            Run(admission: admission), Source));

        Assert.Contains("Admission: Admitted; machine budget CPU slots 2+2/4 (default)", text);
    }

    [Fact]
    public async Task NoRun_ReportsVerdictAndReasonOnly()
    {
        var engine = new StubEngine
        {
            NextVerification = new(VerificationVerdict.NotFullScope, "Test filter 'X' is active.", VerificationSource.None, 0, null, Source),
        };

        var text = await new TestTools(engine, NullMcpCallRecorder.Instance).VerifyAsync(5, CancellationToken.None);

        Assert.Contains("Verdict: NotFullScope", text);
        Assert.Contains("Reason: Test filter 'X' is active.", text);
        Assert.DoesNotContain("get_test_run", text);
        Assert.Equal(TimeSpan.FromSeconds(5), engine.LastVerifyTimeout);
    }

    [Fact]
    public async Task Incomplete_ListsFailedEmptyAndUnfinishedProjectsAndTests()
    {
        var run = Run(TestRunStatus.Incomplete, tests: [Test("A.Pending", TestStatus.NotRun)]) with
        {
            Projects =
            [
                new("Failed.csproj", ProjectRunStatus.Failed, 0),
                new("Empty.csproj", ProjectRunStatus.Completed, 0),
                new("Crashed.csproj", ProjectRunStatus.Crashed, 0),
                new("Good.csproj", ProjectRunStatus.Completed, 1),
            ],
        };
        var text = await VerifyAsync(new(VerificationVerdict.Incomplete, "Incomplete results",
            VerificationSource.Forced, 1, run, Source));

        Assert.Contains("Projects without complete results (3)", text);
        Assert.Contains("Failed.csproj: Failed", text);
        Assert.Contains("Empty.csproj: no test results", text);
        Assert.Contains("Crashed.csproj: Crashed", text);
        Assert.DoesNotContain("Good.csproj", text);
        Assert.Contains("A.Pending: NotRun", text);
    }
}
