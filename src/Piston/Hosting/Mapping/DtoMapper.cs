// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine;
using Piston.Engine.Models;
using Piston.Protocol.Dtos;
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
            result.Source
        );

    public static TestSuiteDto ToDto(this TestSuite suite) =>
        new(
            suite.Name,
            suite.Tests.Select(t => t.ToDto()).ToList(),
            suite.Timestamp,
            suite.TotalDuration.TotalMilliseconds,
            suite.RunId
        );

    public static SourceFingerprintDto ToDto(this SourceFingerprint fingerprint) =>
        new(fingerprint.Algorithm, fingerprint.Value, fingerprint.FileCount);

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
            Build: run.Build?.ToDto());
    }

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
            LastBuiltProject: state.LastBuiltProject
        );
}
