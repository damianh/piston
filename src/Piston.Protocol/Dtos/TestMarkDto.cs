// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>What the run history says about a test that is failing now or flaky.</summary>
/// <param name="FailureKind">New, PreExisting, Flaky or Unknown when the test's latest result failed; otherwise null.</param>
/// <param name="Evidence">The earlier runs the failure kind rests on.</param>
/// <param name="Flaky">True when the test passed and failed on the same source within its recorded history.</param>
/// <param name="Flakiness">Why the test is flaky; null when it is not.</param>
public sealed record TestMarkDto(string Project, string Test, string? FailureKind, string? Evidence, bool Flaky, FlakyEvidenceDto? Flakiness = null, string? ProjectId = null);
