// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Impact;

/// <summary>A recorded unit's identity and state.</summary>
/// <param name="Tests">The tests credited with the unit's files.</param>
/// <param name="HasBranches">True when branch evidence was recorded for all its file rows.</param>
internal sealed record ImpactUnitInfo(
    string UnitKey,
    long RunId,
    bool IsCurrent,
    DateTimeOffset RecordedAt,
    IReadOnlyList<string> Tests,
    bool HasBranches = false)
{
    /// <summary>Framework-qualified test results represented by the measurement, or null for legacy units.</summary>
    public IReadOnlyList<string>? TestResultKeys { get; init; }
}
