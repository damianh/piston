// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Services;

namespace Piston.Engine.Models;

/// <summary>Why a project's run was cut short: a test ran past the test timeout, or it was stopped on request.</summary>
/// <param name="Test">The test's result key; null when the project was stopped with no test running.</param>
/// <param name="Limit">The test timeout it exceeded; null when it was stopped.</param>
/// <param name="Stopped">Whether the run was stopped on request rather than by the test timeout.</param>
public sealed record TestTimeout(string? Test, TimeSpan? Limit, bool Stopped = false)
{
    /// <summary>Describes what happened, such as "T.Slow ran longer than the test timeout of 1 m".</summary>
    public string Describe() => Stopped
        ? Test is null ? "stopped on request" : $"stopped on request while {Test} was running"
        : $"{Test} ran longer than the test timeout of {(Limit is { } limit ? Durations.Format(limit) : "(unknown)")}";
}
