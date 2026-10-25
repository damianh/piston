// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A test result key scoped to the project suite that produced it.</summary>
public sealed record TestOutcomeReferenceDto(string Project, string Key);

/// <summary>Source-input fingerprint captured for a run (see <c>piston-source-v2</c>).</summary>
public sealed record SourceFingerprintDto(string Algorithm, string Value, int FileCount);

/// <summary>
/// Machine-wide execution admission of a run, shared by Piston daemons of the same user.
/// CPU and memory figures are admission reservations, not measured usage.
/// </summary>
public sealed record TestRunAdmissionDto(
    string State,
    int ReservedCpuSlots,
    int? ReservedMemoryMb,
    int BudgetCpuSlots,
    int? BudgetMemoryMb,
    string BudgetSource,
    int QueuedAhead,
    int QueueLength,
    int OtherAdmittedRuns,
    int CpuSlotsInUse,
    long MemoryMbInUse,
    DateTimeOffset QueuedAt,
    DateTimeOffset? AdmittedAt
);

/// <summary>
/// Provenance metadata for a single test run. Enum-like fields are strings so new values can be
/// added without breaking older clients. Per-test results are available through the MCP
/// <c>GetTestRun</c> tool; suites in snapshots carry <see cref="TestSuiteDto.RunId"/>.
/// <see cref="SelectionExplanation"/> says why the run has its scope; <see cref="SelectionReasons"/> says why each
/// test was selected when the impact map narrowed the run by changed lines. <see cref="ChangedFiles"/> are the files
/// whose change triggered the run; <see cref="FixedTests"/> and <see cref="NewlyFailingTests"/> are the project-scoped result keys of
/// tests whose outcome the run changed from failed to passed, and to failed from passed or no result.
/// </summary>
public sealed record TestRunDto(
    string RunId,
    string EngineInstanceId,
    long Sequence,
    string Trigger,
    string Status,
    string? StatusReason,
    string? Scope,
    bool IsFiltered,
    bool IsFullScope,
    string Freshness,
    string? FreshnessReason,
    bool IsCurrentFullResult,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    SourceFingerprintDto? SourceBefore,
    SourceFingerprintDto? SourceAfter,
    bool SourceChangeObserved,
    string? EffectiveFilter,
    IReadOnlyList<string>? TestProjects,
    int TotalTests,
    int Passed,
    int Failed,
    int Skipped,
    int DuplicateTestCount,
    TestRunAdmissionDto? Admission = null,
    BuildResultDto? Build = null,
    string? SelectionExplanation = null,
    IReadOnlyDictionary<string, IReadOnlyList<TestSelectionReasonDto>>? SelectionReasons = null,
    IReadOnlyList<string>? ChangedFiles = null,
    IReadOnlyList<TestOutcomeReferenceDto>? FixedTests = null,
    IReadOnlyList<TestOutcomeReferenceDto>? NewlyFailingTests = null,
    int RetryLimit = 0,
    string? RetriesSkippedReason = null,
    int PassedOnRetry = 0
);
