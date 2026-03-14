namespace Piston.Engine.Models;

public sealed record TestResult(
    string FullyQualifiedName,
    string DisplayName,
    TestStatus Status,
    TimeSpan Duration,
    string? Output,
    string? ErrorMessage,
    string? StackTrace,
    string? Source
);
