// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine;
using Piston.Engine.Models;
using Piston.Engine.Verification;

namespace Piston.Tests.Mcp;

internal sealed class StubEngine : IEngine
{
    public PistonState State { get; } = new();
    public bool ForceRunCalled { get; private set; }
    public string? LastFilter { get; private set; }
    public bool ClearResultsCalled { get; private set; }
    public PistonPhase CompletedPhase { get; set; } = PistonPhase.Idle;
    public Task ForceRunCompletion { get; set; } = Task.CompletedTask;

    /// <summary>Run record published as the latest finished run when ForceRunAsync completes.</summary>
    public TestRunRecord? NextRun { get; set; }

    /// <summary>Histories returned by the history queries; null means history is off.</summary>
    public IReadOnlyList<TestHistory>? Histories { get; set; }

    public string? LastHistoryProject { get; private set; }

    public IReadOnlyList<TestHistory>? GetTestHistory(string test, string? project = null)
    {
        LastHistoryProject = project;
        return Histories?.Where(h => h.Test == test || h.Test.StartsWith(test + " [", StringComparison.Ordinal)).ToList();
    }

    public IReadOnlyList<TestHistory>? GetFlakyTests() => Histories?.Where(h => h.Flaky).ToList();

    /// <summary>Failures returned by <see cref="GetLatestFailures"/>; null means history is off.</summary>
    public IReadOnlyList<FailureClassification>? LatestFailures { get; set; }

    public IReadOnlyList<FailureClassification>? GetLatestFailures() => LatestFailures;

    /// <summary>Runs returned by the stored-run queries; null means history is off.</summary>
    public IReadOnlyList<StoredRun>? StoredRuns { get; set; }

    public IReadOnlyList<StoredRunSummary>? GetStoredRuns() => StoredRuns?.Select(r => r.Summary).ToList();

    public StoredRun? GetStoredRun(string runId) => StoredRuns?.FirstOrDefault(r => r.Summary.RunId == runId);

    public Task StartAsync(string solutionPath)
    {
        State.SolutionPath = solutionPath;
        return Task.CompletedTask;
    }

    public async Task ForceRunAsync()
    {
        ForceRunCalled = true;
        await ForceRunCompletion;
        State.Phase = CompletedPhase;
        if (NextRun is not null)
        {
            State.LastRun = NextRun;
            State.RecentRuns = [NextRun, .. State.RecentRuns];
        }
    }

    public void Stop() { }

    /// <summary>Selection passed to the last <see cref="RunSelectedAsync"/> call.</summary>
    public TestSelection? LastSelection { get; private set; }

    /// <summary>Thrown by <see cref="RunSelectedAsync"/> instead of starting a run.</summary>
    public Exception? RunSelectedError { get; set; }

    /// <summary>The run returned by <see cref="RunSelectedAsync"/>; never completes by default.</summary>
    public Task<TestRunRecord> RunSelectedRun { get; set; } = new TaskCompletionSource<TestRunRecord>().Task;

    public Task<TestRunRecord> RunSelectedAsync(TestSelection selection)
    {
        if (RunSelectedError is { } error)
        {
            throw error;
        }

        LastSelection = selection;
        return RunSelectedRun;
    }

    /// <summary>Result returned by <see cref="VerifyAsync"/>.</summary>
    public VerificationResult? NextVerification { get; set; }

    public TimeSpan? LastVerifyTimeout { get; private set; }

    public Task<VerificationResult> VerifyAsync(TimeSpan timeout, CancellationToken ct)
    {
        LastVerifyTimeout = timeout;
        return Task.FromResult(NextVerification ?? throw new InvalidOperationException("No verification configured."));
    }

    public void SetFilter(string? filter)
    {
        LastFilter = filter;
        State.TestFilter = filter;
    }

    public void ClearResults()
    {
        ClearResultsCalled = true;
        State.TestSuites = [];
        State.CompletedTests = 0;
        State.TotalExpectedTests = 0;
    }

    /// <summary>Coverage report returned by <see cref="GetCoverageReport"/>; null means coverage is disabled.</summary>
    public Engine.Coverage.CoverageReportSnapshot? CoverageReport { get; set; }

    /// <summary>Per-file coverage returned by <see cref="GetFileCoverage"/>, keyed by the requested path.</summary>
    public Dictionary<string, Engine.Coverage.FileCoverageSnapshot> FileCoverage { get; } = [];

    public Engine.Coverage.CoverageReportSnapshot? GetCoverageReport() => CoverageReport;

    /// <summary>Plan returned by <see cref="PlanAffected"/>.</summary>
    public ImpactAnalysisResult? AffectedPlan { get; set; }

    /// <summary>Files passed to the last <see cref="PlanAffected"/> call.</summary>
    public IReadOnlyList<string>? PlannedFiles { get; private set; }

    public ImpactAnalysisResult? PlanAffected(IReadOnlyList<string> filePaths)
    {
        PlannedFiles = filePaths;
        return AffectedPlan;
    }

    public Engine.Coverage.FileCoverageSnapshot? GetFileCoverage(string filePath) =>
        FileCoverage.GetValueOrDefault(filePath);

    /// <summary>Covering tests returned by <see cref="GetCoveringTests"/>, keyed by the requested path.</summary>
    public Dictionary<string, IReadOnlyList<Engine.Coverage.CoveringTest>> CoveringTests { get; } = [];

    public IReadOnlyList<Engine.Coverage.CoveringTest>? GetCoveringTests(string filePath) =>
        CoveringTests.GetValueOrDefault(filePath);

    public void Dispose() { }
}
