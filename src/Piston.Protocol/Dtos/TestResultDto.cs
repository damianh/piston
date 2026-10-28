// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace Piston.Protocol.Dtos;

public sealed record TestResultDto(
    string FullyQualifiedName,
    string DisplayName,
    TestStatusDto Status,
    double DurationMs,
    string? Output,
    string? ErrorMessage,
    string? StackTrace,
    string? Source,
    string? TargetFramework = null,
    IReadOnlyList<TestAttemptDto>? Attempts = null
)
{
    /// <summary>
    /// Identifies this result among a run's results: the fully qualified name, qualified with the
    /// target framework when the test's project ran under more than one.
    /// </summary>
    [JsonIgnore]
    public string Key => TargetFramework is null ? FullyQualifiedName : $"{FullyQualifiedName} [{TargetFramework}]";
}

/// <summary>An earlier attempt of a test that was retried within its run; the result itself is the final attempt.</summary>
public sealed record TestAttemptDto(TestStatusDto Status, double DurationMs, string? ErrorMessage, string? StackTrace);
