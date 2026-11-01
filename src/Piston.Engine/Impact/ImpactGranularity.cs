// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Impact;

/// <summary>
/// How precisely an impact-map entry attributes executed code to tests. Higher values are finer.
/// </summary>
internal enum ImpactGranularity
{
    /// <summary>Every test in a project's run is credited with everything the run executed.</summary>
    Run = 0,

    /// <summary>Every test in one class is credited with what that class's tests executed when run alone.</summary>
    Class = 1,

    /// <summary>The test was measured on its own.</summary>
    Test = 2,
}
