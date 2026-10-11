// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

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
