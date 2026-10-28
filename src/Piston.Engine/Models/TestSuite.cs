// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace Piston.Engine.Models;

public sealed record TestSuite(
    string Name,
    IReadOnlyList<TestResult> Tests,
    DateTimeOffset Timestamp,
    TimeSpan TotalDuration
)
{
    /// <summary>Solution-relative project path that identifies the project, when known.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Stable key for this suite, falling back to its name for non-project targets.</summary>
    public string Key => ProjectId ?? Name;

    /// <summary>Identifier of the <see cref="TestRunRecord"/> that produced this suite, when known.</summary>
    public string? RunId { get; init; }

    /// <summary>What Piston did to run the process that produced this suite, when it was recorded.</summary>
    [JsonIgnore]
    public ExecutionLog? ExecutionLog { get; init; }
}
