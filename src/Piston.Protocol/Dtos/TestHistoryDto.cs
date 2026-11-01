// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>Recorded results of one test, oldest first.</summary>
/// <param name="Flakiness">Why the test is flaky; null when it is not.</param>
public sealed record TestHistoryDto(string Project, string Test, bool Flaky, IReadOnlyList<TestHistoryEntryDto> Runs, FlakyEvidenceDto? Flakiness = null, string? ProjectId = null);
