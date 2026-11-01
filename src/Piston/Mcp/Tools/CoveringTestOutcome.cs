// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Mcp.Tools;

/// <summary>A test's latest outcome, combined over the frameworks it ran under.</summary>
internal enum CoveringTestOutcome
{
    Passed,
    Failed,

    /// <summary>Skipped everywhere it ran: it executed nothing, so it is not evidence either way.</summary>
    Skipped,

    /// <summary>No result, e.g. not run since it was discovered or still running.</summary>
    NoResult,
}
