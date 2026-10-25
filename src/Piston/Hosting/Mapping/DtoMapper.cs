// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine;
using Piston.Engine.Coverage;
using Piston.Engine.Models;
using Piston.Hosting.Services;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;

namespace Piston.Hosting.Mapping;

public static class DtoMapper
{
    public static ActivityEvent ToDto(this EngineActivityEvent activity)
    {
        var dto = activity switch
        {
            SolutionLoadedActivityEvent loaded =>
                ActivityEventFactory.SolutionLoaded(loaded.LoadedSolutionPath, loaded.ProjectCount),
            FileChangesDetectedActivityEvent changed =>
                ActivityEventFactory.FileChangesDetected(changed.SolutionPath, changed.ChangedFiles),
            BuildCompletedActivityEvent build =>
                ActivityEventFactory.BuildCompleted(build.SolutionPath, build.Succeeded, build.DurationMs, build.Errors),
            TestRunCompletedActivityEvent tests =>
                ActivityEventFactory.TestRunCompleted(tests.SolutionPath, tests.Passed, tests.Failed, tests.Skipped,
                    tests.DurationMs, tests.Failures.Select(f => new TestFailureData(f.FullyQualifiedName, f.Message)).ToList()),
            _ => throw new ArgumentOutOfRangeException(nameof(activity), activity, "Unknown engine activity event."),
        };

        return dto with { Timestamp = activity.Timestamp };
    }

    public static TestStatusDto ToDto(this TestStatus status) => status switch
    {
        TestStatus.NotRun => TestStatusDto.NotRun,
        TestStatus.Running => TestStatusDto.Running,
        TestStatus.Passed => TestStatusDto.Passed,
        TestStatus.Failed => TestStatusDto.Failed,
        TestStatus.Skipped => TestStatusDto.Skipped,
        _ => TestStatusDto.NotRun,
    };

    public static BuildStatusDto ToDto(this BuildStatus status) => status switch
    {
        BuildStatus.None => BuildStatusDto.None,
        BuildStatus.Building => BuildStatusDto.Building,
        BuildStatus.Succeeded => BuildStatusDto.Succeeded,
        BuildStatus.Failed => BuildStatusDto.Failed,
        BuildStatus.Skipped => BuildStatusDto.Skipped,
        _ => BuildStatusDto.None,
    };

    public static PistonPhaseDto ToDto(this PistonPhase phase) => phase switch
    {
        PistonPhase.Idle => PistonPhaseDto.Idle,
        PistonPhase.Watching => PistonPhaseDto.Watching,
        PistonPhase.Analyzing => PistonPhaseDto.Analyzing,
        PistonPhase.Building => PistonPhaseDto.Building,
        PistonPhase.Testing => PistonPhaseDto.Testing,
        PistonPhase.Error => PistonPhaseDto.Error,
        _ => PistonPhaseDto.Idle,
    };

    public static ProjectRunStatusDto ToDto(this ProjectRunStatus status) => status switch
    {
        ProjectRunStatus.Pending => ProjectRunStatusDto.Pending,
        ProjectRunStatus.Running => ProjectRunStatusDto.Running,
        ProjectRunStatus.Completed => ProjectRunStatusDto.Completed,
        ProjectRunStatus.Failed => ProjectRunStatusDto.Failed,
        ProjectRunStatus.Crashed => ProjectRunStatusDto.Crashed,
        ProjectRunStatus.TimedOut => ProjectRunStatusDto.TimedOut,
        _ => ProjectRunStatusDto.Pending,
    };

    public static TestResultDto ToDto(this TestResult result) =>
        new(
            result.FullyQualifiedName,
            result.DisplayName,
            result.Status.ToDto(),
            result.Duration.TotalMilliseconds,
            result.Output,
            result.ErrorMessage,
            result.StackTrace,
            result.Source,
            result.TargetFramework,
            result.Attempts.Count == 0
                ? null
                : [.. result.Attempts.Select(a => new TestAttemptDto(a.Status.ToDto(), a.Duration.TotalMilliseconds, a.ErrorMessage, a.StackTrace))]
        );

    public static TestSuiteDto ToDto(this TestSuite suite) =>
        new(
            suite.Name,
            suite.Tests.Select(t => t.ToDto()).ToList(),
            suite.Timestamp,
            suite.TotalDuration.TotalMilliseconds,
            suite.RunId
        );

    /// <summary>
    /// Most bytes a run log's entries may take once serialized. Half the named-pipe message limit leaves room
    /// for the rest of the response and its JSON-RPC envelope.
    /// </summary>
    private const long RunLogEntriesBudget = MessageFramer.DefaultMaxMessageSize / 2;

    // Upper bound for an entry's timestamp, property names and punctuation, and for an omitted marker.
    private const long LogEntryOverhead = 96;
    private const long OmittedMarkerAllowance = 256;

    public static ExecutionLogDto ToDto(this ExecutionLog log, string? runId)
    {
        var (entries, omitted) = FitToBudget(log.Entries, log.OmittedEntries, RunLogEntriesBudget);
        return new(
            log.ProjectPath,
            log.Runner,
            log.ExitCode,
            log.StartedAt,
            log.CompletedAt,
            omitted,
            [.. entries.Select(e => new ExecutionLogEntryDto(e.Timestamp, e.Category, e.Message))],
            runId
        );
    }

    /// <summary>
    /// Keeps as much of the start and end of <paramref name="entries"/> as fits in <paramref name="budget"/>
    /// serialized bytes, replacing the dropped middle, and any existing omitted marker, with one marker.
    /// </summary>
    private static (IReadOnlyList<ExecutionLogEntry> Entries, int Omitted) FitToBudget(
        IReadOnlyList<ExecutionLogEntry> entries, int omitted, long budget)
    {
        var sizes = new long[entries.Count];
        long total = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            sizes[i] = EstimateJsonSize(entries[i]);
            total += sizes[i];
        }

        if (total <= budget)
        {
            return (entries, omitted);
        }

        budget -= OmittedMarkerAllowance;

        // The dropped range must cover an existing marker so the result has only one.
        var marker = -1;
        for (var i = 0; i < entries.Count && marker < 0; i++)
        {
            if (string.Equals(entries[i].Category, ExecutionLog.OmittedCategory, StringComparison.Ordinal))
            {
                marker = i;
            }
        }

        var headLimit = marker >= 0 ? marker : entries.Count;
        var tailLimit = marker + 1;

        // Half the budget for the head, the rest for the tail, then any left over back to the head.
        long used = 0;
        var head = 0;
        while (head < headLimit && used + sizes[head] <= budget / 2)
        {
            used += sizes[head++];
        }

        var tail = entries.Count;
        while (tail > Math.Max(head, tailLimit) && used + sizes[tail - 1] <= budget)
        {
            used += sizes[--tail];
        }

        while (head < Math.Min(tail, headLimit) && used + sizes[head] <= budget)
        {
            used += sizes[head++];
        }

        // The existing marker in the dropped range stands for the entries it already counted.
        var dropped = tail - head + (marker >= 0 ? omitted - 1 : 0);
        var timestamp = entries[Math.Min(tail, entries.Count - 1)].Timestamp;

        List<ExecutionLogEntry> kept = new(head + 1 + entries.Count - tail);
        for (var i = 0; i < head; i++)
        {
            kept.Add(entries[i]);
        }

        kept.Add(ExecutionLog.OmittedMarker(timestamp, dropped));
        for (var i = tail; i < entries.Count; i++)
        {
            kept.Add(entries[i]);
        }

        return (kept, dropped);
    }

    private static long EstimateJsonSize(ExecutionLogEntry entry) =>
        LogEntryOverhead + EstimateJsonSize(entry.Category) + EstimateJsonSize(entry.Message);

    // Worst case: anything but plain printable ASCII may be written as a six-byte \uXXXX escape.
    private static long EstimateJsonSize(string value)
    {
        long size = 0;
        foreach (var c in value)
        {
            size += c is >= ' ' and <= '~' and not ('"' or '\\' or '<' or '>' or '&' or '\'' or '+' or '`') ? 1 : 6;
        }

        return size;
    }

    /// <summary>
    /// The log of the latest run of the test project named <paramref name="project"/> (one process covers
    /// all of its target frameworks), or <see langword="null"/> when none was recorded.
    /// </summary>
    public static ExecutionLogDto? FindRunLog(this PistonState state, string project)
    {
        // A selective run leaves the project's untouched results in the previous suite, so take the latest.
        var latest = state.TestSuites
            .Where(s => s.ExecutionLog is not null && string.Equals(s.Name, project, StringComparison.Ordinal))
            .MaxBy(s => s.Timestamp);

        return latest?.ExecutionLog!.ToDto(latest.RunId);
    }

    public static SourceFingerprintDto ToDto(this SourceFingerprint fingerprint) =>
        new(fingerprint.Algorithm, fingerprint.Value, fingerprint.FileCount);

    public static StoredRunSummaryDto ToDto(this StoredRunSummary run) => new(
        run.RunId, run.Trigger.ToString(), run.Status.ToString(), run.Scope?.ToString(), run.IsFullScope, run.Freshness.ToString(),
        run.RequestedAt, run.CompletedAt, run.Source, run.Passed, run.Failed, run.Skipped, run.Retried, run.PassedOnRetry);

    public static StoredRunDto ToDto(this StoredRun run) => new(
        run.Summary.ToDto(),
        [.. run.Results.Select(r => new StoredTestResultDto(r.Project, r.Test, r.Status.ToString(), r.DurationMs, r.ErrorMessage, r.StackTrace,
            r.Attempts is { Count: > 0 } attempts ? [.. attempts.Select(a => new StoredAttemptDto(a.Status.ToString(), a.DurationMs, a.ErrorMessage))] : null))]);

    public static TestHistoryDto ToDto(this TestHistory history) => new(
        history.Project, history.Test, history.Flaky,
        [.. history.Runs.Select(e => new TestHistoryEntryDto(e.RunId, e.At, e.Status.ToString(), e.DurationMs, e.Source, e.Attempt))],
        history.Flakiness?.ToDto());

    public static FlakyEvidenceDto ToDto(this FlakinessEvidence evidence) => new(
        evidence.Source, evidence.Passed, evidence.Failed, evidence.RetryOutcomes, evidence.OtherWorktrees, evidence.Describe(),
        [.. evidence.Outcomes.Select(o => new FlakyOutcomeDto(o.RunId, o.At, o.Status.ToString(), o.Attempt, o.Worktree))]);

    /// <summary>One mark per test that is failing now or flaky.</summary>
    public static List<TestMarkDto> ToMarks(IReadOnlyList<FailureClassification> failures, IReadOnlyList<TestHistory> flaky)
    {
        var flakyByKey = flaky.GroupBy(f => (f.Project, f.Test)).ToDictionary(g => g.Key, g => g.First().Flakiness);
        var marks = failures
            .Select(f =>
            {
                var evidence = f.Flakiness ?? flakyByKey.GetValueOrDefault((f.Project, f.Test));
                return new TestMarkDto(f.Project, f.Test, f.Kind.ToString(), f.Evidence,
                    evidence is not null || flakyByKey.ContainsKey((f.Project, f.Test)), evidence?.ToDto());
            })
            .ToList();
        var failing = failures.Select(f => (f.Project, f.Test)).ToHashSet();
        marks.AddRange(flaky.Where(f => !failing.Contains((f.Project, f.Test)))
            .Select(f => new TestMarkDto(f.Project, f.Test, null, null, Flaky: true, f.Flakiness?.ToDto())));
        return marks;
    }

    public static TestRunDto ToDto(this TestRunRecord run)
    {
        var tests = run.Tests.ToList();
        return new TestRunDto(
            RunId: run.RunId,
            EngineInstanceId: run.EngineInstanceId,
            Sequence: run.Sequence,
            Trigger: run.Trigger.ToString(),
            Status: run.Status.ToString(),
            StatusReason: run.StatusReason,
            Scope: run.Scope?.ToString(),
            IsFiltered: run.IsFiltered,
            IsFullScope: run.IsFullScope,
            Freshness: run.Freshness.ToString(),
            FreshnessReason: run.FreshnessReason,
            IsCurrentFullResult: run.IsCurrentFullResult,
            RequestedAt: run.RequestedAt,
            StartedAt: run.StartedAt,
            CompletedAt: run.CompletedAt,
            SourceBefore: run.SourceBefore?.ToDto(),
            SourceAfter: run.SourceAfter?.ToDto(),
            SourceChangeObserved: run.SourceChangeObserved,
            EffectiveFilter: run.Context?.EffectiveFilter,
            TestProjects: run.Context?.TestProjects,
            TotalTests: tests.Count,
            Passed: tests.Count(t => t.Status == TestStatus.Passed),
            Failed: tests.Count(t => t.Status == TestStatus.Failed),
            Skipped: tests.Count(t => t.Status == TestStatus.Skipped),
            DuplicateTestCount: run.DuplicateTestIds.Count,
            Admission: run.Admission?.ToDto(),
            Build: run.Build?.ToDto(),
            SelectionExplanation: run.Context?.SelectionExplanation,
            SelectionReasons: run.Context?.SelectionReasons?.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<TestSelectionReasonDto>)[.. kv.Value.Select(r => r.ToDto())],
                StringComparer.Ordinal),
            ChangedFiles: run.ChangedFiles,
            FixedTests: [.. run.FixedTests.Select(t => new TestOutcomeReferenceDto(t.Project, t.Key))],
            NewlyFailingTests: [.. run.NewlyFailingTests.Select(t => new TestOutcomeReferenceDto(t.Project, t.Key))],
            RetryLimit: run.RetryLimit,
            RetriesSkippedReason: run.RetriesSkippedReason,
            PassedOnRetry: tests.Count(t => t.Status == TestStatus.Passed && t.Attempts.Count > 0));
    }

    public static TestSelectionReasonDto ToDto(this TestSelectionReason reason) =>
        new(reason.FilePath, reason.ChangedLines, reason.ExecutedLines, reason.Granularity, reason.ProjectPath);

    public static CoveringTestsDto ToCoveringTestsDto(string filePath, IReadOnlyList<CoveringTest>? tests) =>
        new(filePath, [.. (tests ?? []).Select(t => new CoveringTestDto(
            t.TestFqn,
            t.ProjectPath,
            t.Granularity,
            [.. t.ExecutedLines.Select(r => new LineRangeDto(r.Start, r.End))],
            t.IsCurrent))]);

    public static TestRunAdmissionDto ToDto(this TestRunAdmission admission) =>
        new(
            State: admission.State.ToString(),
            ReservedCpuSlots: admission.ReservedCpuSlots,
            ReservedMemoryMb: admission.ReservedMemoryMb,
            BudgetCpuSlots: admission.BudgetCpuSlots,
            BudgetMemoryMb: admission.BudgetMemoryMb,
            BudgetSource: admission.BudgetSource,
            QueuedAhead: admission.QueuedAhead,
            QueueLength: admission.QueueLength,
            OtherAdmittedRuns: admission.OtherAdmittedRuns,
            CpuSlotsInUse: admission.CpuSlotsInUse,
            MemoryMbInUse: admission.MemoryMbInUse,
            QueuedAt: admission.QueuedAt,
            AdmittedAt: admission.AdmittedAt);

    public static BuildResultDto ToDto(this BuildResult result) =>
        new(
            result.Status.ToDto(),
            result.Errors,
            result.Warnings,
            result.Duration.TotalMilliseconds
        );

    public static StateSnapshotNotification ToSnapshot(this PistonState state) =>
        new(
            Phase: state.Phase.ToDto(),
            Suites: state.TestSuites.Select(s => s.ToDto()).ToList(),
            InProgressSuites: state.InProgressSuites.Select(s => s.ToDto()).ToList(),
            LastBuild: state.LastBuild?.ToDto(),
            LastRunTime: state.LastRunTime,
            LastBuildDurationMs: state.LastBuildDuration?.TotalMilliseconds,
            LastTestDurationMs: state.LastTestDuration?.TotalMilliseconds,
            LastTestRunnerError: state.LastTestRunnerError,
            TotalExpectedTests: state.TotalExpectedTests,
            CompletedTests: state.CompletedTests,
            VerifiedSinceChangeCount: state.VerifiedSinceChangeCount,
            LastFileChangeTime: state.LastFileChangeTime,
            SolutionPath: state.SolutionPath,
            AffectedProjects: state.AffectedProjects,
            AffectedTestProjects: state.AffectedTestProjects,
            LastChangedFiles: state.LastChangedFiles,
            CoverageEnabled: state.CoverageEnabled,
            HasCoverageData: state.HasCoverageData,
            CoverageImpactDetail: state.CoverageImpactDetail,
            TotalTestProjects: state.TotalTestProjects,
            CompletedTestProjects: state.CompletedTestProjects,
            ProjectStatuses: state.ProjectStatuses.Count > 0
                ? state.ProjectStatuses.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToDto())
                : null,
            LastRun: state.LastRun?.ToDto(),
            ActiveRun: state.CurrentRun?.ToDto(),
            TotalBuildProjects: state.TotalBuildProjects,
            CompletedBuildProjects: state.CompletedBuildProjects,
            LastBuiltProject: state.LastBuiltProject,
            ImpactBackfill: state.ImpactBackfill is { } backfill
                ? new ImpactBackfillDto(backfill.Granularity, backfill.Completed, backfill.Total, backfill.CurrentUnit, backfill.ProjectPath)
                : null,
            LongestRunningTest: state.LongestRunningTest is { } running
                ? new RunningTestDto(running.Name, running.ProjectPath, running.StartedAt, running.PossiblyHanging)
                : null,
            RetryStatus: state.RetryStatus
        );

    public static CoverageSummaryDto ToCoverageSummaryDto(CoverageReportSnapshot? report, string? solutionDirectory) =>
        report is null
            ? new CoverageSummaryDto(false, null, 0, 0, 0, 0, [])
            : new CoverageSummaryDto(
                Enabled: true,
                GeneratedAt: report.GeneratedAt,
                LinesTotal: report.LinesTotal,
                LinesCovered: report.LinesCovered,
                BranchesTotal: report.BranchesTotal,
                BranchesCovered: report.BranchesCovered,
                Files: [.. report.Files.Select(f => f.ToDto(solutionDirectory))]);

    public static CoverageFileSummaryDto ToDto(this CoverageFileSummary file, string? solutionDirectory) => new(
        FilePath: file.FilePath,
        RelativePath: solutionDirectory is null
            ? file.FilePath
            : Path.GetRelativePath(solutionDirectory, file.FilePath).Replace('\\', '/'),
        Module: file.Module,
        LinesTotal: file.LinesTotal,
        LinesCovered: file.LinesCovered,
        BranchesTotal: file.BranchesTotal,
        BranchesCovered: file.BranchesCovered,
        IsStale: file.IsStale,
        IsPartial: file.IsPartial,
        UpdatedAt: file.UpdatedAt);

    public static CoverageLineDto ToDto(this LineCoverage line) => new(
        line.LineNumber,
        line.HitCount,
        line.HitCount == 0 ? "uncovered"
            : line.BranchesTotal > 0 && line.BranchesCovered < line.BranchesTotal ? "partial"
            : "covered",
        line.BranchesCovered,
        line.BranchesTotal);

    public static FileCoverageDto ToFileCoverageDto(
        string requestedPath, FileCoverageSnapshot? coverage, string? solutionDirectory, bool includeSource) =>
        coverage is null
            ? new FileCoverageDto(requestedPath, [])
            : new FileCoverageDto(
                coverage.File.FilePath,
                [.. coverage.Lines.Select(l => l.ToDto())],
                coverage.File.ToDto(solutionDirectory),
                includeSource ? SourceFileReader.Read(coverage.File.FilePath, solutionDirectory).Lines : null);
}
