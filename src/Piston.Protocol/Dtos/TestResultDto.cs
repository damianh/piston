namespace Piston.Protocol.Dtos;

public sealed record TestResultDto(
    string FullyQualifiedName,
    string DisplayName,
    TestStatusDto Status,
    double DurationMs,
    string? Output,
    string? ErrorMessage,
    string? StackTrace,
    string? Source
);
