namespace Piston.Engine.Models;

public sealed record TestSuite(
    string Name,
    IReadOnlyList<TestResult> Tests,
    DateTimeOffset Timestamp,
    TimeSpan TotalDuration
)
{
    /// <summary>Identifier of the <see cref="TestRunRecord"/> that produced this suite, when known.</summary>
    public string? RunId { get; init; }
}
