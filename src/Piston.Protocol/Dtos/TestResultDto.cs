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
    string? TargetFramework = null
)
{
    /// <summary>
    /// Identifies this result among a run's results: the fully qualified name, qualified with the
    /// target framework when the test's project ran under more than one.
    /// </summary>
    [JsonIgnore]
    public string Key => TargetFramework is null ? FullyQualifiedName : $"{FullyQualifiedName} [{TargetFramework}]";
}
