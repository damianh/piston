// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine;

/// <summary>How finely the idle-time impact backfill measures which tests execute which lines.</summary>
public enum ImpactBackfillMode
{
    /// <summary>No backfill: tests are attributed to lines per test project.</summary>
    Off,

    /// <summary>Measure each test class separately when idle.</summary>
    Class,

    /// <summary>Measure each test method separately when idle; precise but slow.</summary>
    Test,
}
