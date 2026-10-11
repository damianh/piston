namespace Piston.Protocol.Dtos;

/// <summary>Source-input fingerprint captured for a run (see <c>piston-source-v1</c>).</summary>
public sealed record SourceFingerprintDto(string Algorithm, string Value, int FileCount);

/// <summary>
/// Provenance metadata for a single test run. Enum-like fields are strings so new values can be
/// added without breaking older clients. Per-test results are available through the MCP
/// <c>GetTestRun</c> tool; suites in snapshots carry <see cref="TestSuiteDto.RunId"/>.
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
    int DuplicateTestCount
);
