// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Mcp.Tools;

/// <summary>What the coverage evidence says about one changed line.</summary>
internal enum ChangedLineVerdict
{
    /// <summary>The line ran, and every current test credited with executing it passed.</summary>
    CoveredByPassing,

    /// <summary>The line ran, and every current test credited with executing it failed.</summary>
    CoveredOnlyByFailing,

    /// <summary>The line is executable and no test that ran executed it.</summary>
    Uncovered,

    /// <summary>The file was measured as it is now and the line has no executable code.</summary>
    NonExecutable,

    /// <summary>The evidence cannot prove anything about the line; see <see cref="UnknownReason"/>.</summary>
    Unknown,
}
