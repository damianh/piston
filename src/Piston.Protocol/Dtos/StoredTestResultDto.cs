// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>One test's result in a stored run.</summary>
/// <param name="Project">Test project name.</param>
/// <param name="Test">Fully-qualified name, with <c> [tfm]</c> when multi-targeted.</param>
/// <param name="Attempts">Earlier attempts of a test retried within the run, oldest first; null when it ran once.</param>
public sealed record StoredTestResultDto(
    string Project,
    string Test,
    string Status,
    double DurationMs,
    string? ErrorMessage,
    string? StackTrace,
    IReadOnlyList<StoredAttemptDto>? Attempts = null,
    string? ProjectId = null);
