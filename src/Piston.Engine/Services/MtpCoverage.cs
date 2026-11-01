// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Services;

/// <summary>Whether, and why not, an MTP test run collects code coverage.</summary>
internal enum MtpCoverage
{
    Off,
    Collect,

    /// <summary>Coverage was requested but the test app cannot collect it.</summary>
    ExtensionMissing,
}
