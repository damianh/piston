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
    /// The fully qualified name of the test method (<c>Ns.Class.Method</c>), when the runner reported it. It
    /// differs from <see cref="FullyQualifiedName"/> when the test has a custom display name, and is what a
    /// <c>FullyQualifiedName</c> filter matches.
    /// </summary>
    public string? Method { get; init; }

    /// <summary>
    /// Identifies this result among a run's results: the fully qualified name, qualified with the
    /// target framework when it is known.
    /// </summary>
    [JsonIgnore]
    public string ResultKey => TargetFramework is null ? FullyQualifiedName : $"{FullyQualifiedName} [{TargetFramework}]";

    /// <summary>
    /// Earlier attempts of this test in the same run, oldest first, when failed tests were retried; empty
    /// otherwise. This result is the last attempt, so a test that passed on retry is <see cref="TestStatus.Passed"/>.
    /// </summary>
    public IReadOnlyList<TestAttempt> Attempts { get; init; } = [];
}

/// <summary>One earlier attempt of a test that was retried within a run.</summary>
/// <param name="Status">Outcome of the attempt.</param>
/// <param name="Duration">How long the attempt took.</param>
/// <param name="ErrorMessage">Failure message, when it failed.</param>
/// <param name="StackTrace">Failure stack trace, when it failed.</param>
public sealed record TestAttempt(TestStatus Status, TimeSpan Duration, string? ErrorMessage, string? StackTrace);
