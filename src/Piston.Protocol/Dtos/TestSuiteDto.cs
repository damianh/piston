namespace Piston.Protocol.Dtos;

public sealed record TestSuiteDto(
    string Name,
    IReadOnlyList<TestResultDto> Tests,
    DateTimeOffset Timestamp,
    double TotalDurationMs
);
