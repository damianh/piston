// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

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
)
{
    /// <summary>
    /// The target framework the test ran under (e.g. <c>net8.0</c>), set when its project produced results
    /// for more than one framework: a multi-targeted project runs each test once per framework.
    /// </summary>
    public string? TargetFramework { get; init; }

    /// <summary>
    /// Identifies this result among a run's results: the fully qualified name, qualified with the
    /// target framework when it is known.
    /// </summary>
    [JsonIgnore]
    public string ResultKey => TargetFramework is null ? FullyQualifiedName : $"{FullyQualifiedName} [{TargetFramework}]";
}
