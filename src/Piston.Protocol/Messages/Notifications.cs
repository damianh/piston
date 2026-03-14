using Piston.Protocol.Dtos;

namespace Piston.Protocol.Messages;

public sealed record PhaseChangedNotification(
    PistonPhaseDto Phase,
    string? Detail
);

public sealed record StateSnapshotNotification(
    PistonPhaseDto Phase,
    IReadOnlyList<TestSuiteDto> Suites,
    IReadOnlyList<TestSuiteDto> InProgressSuites,
    BuildResultDto? LastBuild,
    DateTimeOffset? LastRunTime,
    double? LastBuildDurationMs,
    double? LastTestDurationMs,
    string? LastTestRunnerError,
    int TotalExpectedTests,
    int CompletedTests,
    int VerifiedSinceChangeCount,
    DateTimeOffset? LastFileChangeTime,
    string? SolutionPath,
    IReadOnlyList<string>? AffectedProjects,
    IReadOnlyList<string>? AffectedTestProjects,
    IReadOnlyList<string>? LastChangedFiles,
    bool CoverageEnabled,
    bool HasCoverageData,
    string? CoverageImpactDetail,
    int TotalTestProjects,
    int CompletedTestProjects,
    IReadOnlyDictionary<string, ProjectRunStatusDto>? ProjectStatuses
);

public sealed record TestProgressNotification(
    IReadOnlyList<TestSuiteDto> InProgressSuites,
    int CompletedTests,
    int TotalExpectedTests
);

public sealed record BuildErrorNotification(
    BuildResultDto Build
);
