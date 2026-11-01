// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>An earlier attempt of a test that was retried within its run; the result itself is the final attempt.</summary>
public sealed record TestAttemptDto(TestStatusDto Status, double DurationMs, string? ErrorMessage, string? StackTrace);
