// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>An earlier attempt of a test retried within a stored run.</summary>
public sealed record StoredAttemptDto(string Status, double DurationMs, string? ErrorMessage);
