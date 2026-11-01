// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>An earlier attempt of a test that was retried within a stored run. The message is kept for failures only, clipped.</summary>
public sealed record StoredAttempt(TestStatus Status, double DurationMs, string? ErrorMessage = null);
