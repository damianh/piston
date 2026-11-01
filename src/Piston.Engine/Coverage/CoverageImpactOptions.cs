// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>The units a coverage run measured, for the impact map.</summary>
/// <param name="Units">Each unit with the reports that measured it.</param>
/// <param name="Granularity">How the units were measured.</param>
/// <param name="StartedUtc">
/// When the run's build started. Files modified since may differ from what was measured, so they are not
/// recorded.
/// </param>
internal sealed record CoverageImpactOptions(
    IReadOnlyList<CoverageRunUnit> Units,
    Impact.ImpactGranularity Granularity,
    DateTime StartedUtc);
