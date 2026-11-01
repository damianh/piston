// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Recorded results of one test, oldest first.</summary>
/// <param name="Flakiness">Why the test is flaky; null when it is not.</param>
public sealed record TestHistory(string Project, string Test, IReadOnlyList<TestHistoryEntry> Runs, FlakinessEvidence? Flakiness)
{
    public bool Flaky => Flakiness is not null;
    public string ProjectId { get; init; } = Project;
}
