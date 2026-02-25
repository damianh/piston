namespace Piston.Core.Models;

public sealed record TestSuite(
    string Name,
    IReadOnlyList<TestResult> Tests,
    DateTimeOffset Timestamp,
    TimeSpan TotalDuration
);
