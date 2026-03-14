using Piston.Engine;
using Piston.Engine.Models;
using Piston.Protocol.Dtos;
using Piston.Protocol.Messages;

namespace Piston.Controller.Mapping;

internal static class DtoMapper
{
    internal static TestStatusDto ToDto(this TestStatus status) => status switch
    {
        TestStatus.NotRun   => TestStatusDto.NotRun,
        TestStatus.Running  => TestStatusDto.Running,
        TestStatus.Passed   => TestStatusDto.Passed,
        TestStatus.Failed   => TestStatusDto.Failed,
        TestStatus.Skipped  => TestStatusDto.Skipped,
        _                   => TestStatusDto.NotRun,
    };

    internal static BuildStatusDto ToDto(this BuildStatus status) => status switch
    {
        BuildStatus.None       => BuildStatusDto.None,
        BuildStatus.Building   => BuildStatusDto.Building,
        BuildStatus.Succeeded  => BuildStatusDto.Succeeded,
        BuildStatus.Failed     => BuildStatusDto.Failed,
        _                      => BuildStatusDto.None,
    };

    internal static PistonPhaseDto ToDto(this PistonPhase phase) => phase switch
    {
        PistonPhase.Idle      => PistonPhaseDto.Idle,
        PistonPhase.Watching  => PistonPhaseDto.Watching,
        PistonPhase.Analyzing => PistonPhaseDto.Analyzing,
        PistonPhase.Building  => PistonPhaseDto.Building,
        PistonPhase.Testing   => PistonPhaseDto.Testing,
        PistonPhase.Error     => PistonPhaseDto.Error,
        _                     => PistonPhaseDto.Idle,
    };

    internal static ProjectRunStatusDto ToDto(this ProjectRunStatus status) => status switch
    {
        ProjectRunStatus.Pending   => ProjectRunStatusDto.Pending,
        ProjectRunStatus.Running   => ProjectRunStatusDto.Running,
        ProjectRunStatus.Completed => ProjectRunStatusDto.Completed,
        ProjectRunStatus.Failed    => ProjectRunStatusDto.Failed,
        ProjectRunStatus.Crashed   => ProjectRunStatusDto.Crashed,
        _                          => ProjectRunStatusDto.Pending,
    };

    internal static TestResultDto ToDto(this TestResult result) =>
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

    internal static TestSuiteDto ToDto(this TestSuite suite) =>
        new(
            suite.Name,
            suite.Tests.Select(t => t.ToDto()).ToList(),
            suite.Timestamp,
            suite.TotalDuration.TotalMilliseconds
        );

    internal static BuildResultDto ToDto(this BuildResult result) =>
        new(
            result.Status.ToDto(),
            result.Errors,
            result.Warnings,
            result.Duration.TotalMilliseconds
        );

    internal static StateSnapshotNotification ToSnapshot(this PistonState state) =>
        new(
            Phase:                  state.Phase.ToDto(),
            Suites:                 state.TestSuites.Select(s => s.ToDto()).ToList(),
            InProgressSuites:       state.InProgressSuites.Select(s => s.ToDto()).ToList(),
            LastBuild:              state.LastBuild?.ToDto(),
            LastRunTime:            state.LastRunTime,
            LastBuildDurationMs:    state.LastBuildDuration?.TotalMilliseconds,
            LastTestDurationMs:     state.LastTestDuration?.TotalMilliseconds,
            LastTestRunnerError:    state.LastTestRunnerError,
            TotalExpectedTests:     state.TotalExpectedTests,
            CompletedTests:         state.CompletedTests,
            VerifiedSinceChangeCount: state.VerifiedSinceChangeCount,
            LastFileChangeTime:     state.LastFileChangeTime,
            SolutionPath:           state.SolutionPath,
            AffectedProjects:       state.AffectedProjects,
            AffectedTestProjects:   state.AffectedTestProjects,
            LastChangedFiles:       state.LastChangedFiles,
            CoverageEnabled:        state.CoverageEnabled,
            HasCoverageData:        state.HasCoverageData,
            CoverageImpactDetail:   state.CoverageImpactDetail,
            TotalTestProjects:      state.TotalTestProjects,
            CompletedTestProjects:  state.CompletedTestProjects,
            ProjectStatuses:        state.ProjectStatuses.Count > 0
                ? state.ProjectStatuses.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToDto())
                : null
        );
}
