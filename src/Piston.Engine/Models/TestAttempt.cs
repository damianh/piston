// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>One earlier attempt of a test that was retried within a run.</summary>
/// <param name="Status">Outcome of the attempt.</param>
/// <param name="Duration">How long the attempt took.</param>
/// <param name="ErrorMessage">Failure message, when it failed.</param>
/// <param name="StackTrace">Failure stack trace, when it failed.</param>
public sealed record TestAttempt(TestStatus Status, TimeSpan Duration, string? ErrorMessage, string? StackTrace);
