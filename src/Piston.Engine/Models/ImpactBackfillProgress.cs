// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Progress of the idle-time measurement of which test classes (or tests) execute which lines.</summary>
/// <param name="Granularity"><c>class</c> or <c>test</c>.</param>
/// <param name="Completed">Units measured so far in this pass.</param>
/// <param name="Total">Units this pass measures.</param>
/// <param name="CurrentUnit">The class or test being measured.</param>
/// <param name="ProjectPath">The test project of <paramref name="CurrentUnit"/>.</param>
public sealed record ImpactBackfillProgress(
    string Granularity,
    int Completed,
    int Total,
    string? CurrentUnit,
    string? ProjectPath);
