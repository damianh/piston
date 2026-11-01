// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Mcp.Tools;

/// <summary>Why a changed line's verdict is <see cref="ChangedLineVerdict.Unknown"/>.</summary>
internal enum UnknownReason
{
    None,

    /// <summary>
    /// The line's code changed after it was measured, or (for a range) the file differs from the working tree: the
    /// coverage does not describe the line. Lines in unchanged C# members of a changed file keep their remapped
    /// coverage instead.
    /// </summary>
    Stale,

    /// <summary>No coverage run measured the file, e.g. it is not compiled into a tested assembly.</summary>
    NotMeasured,

    /// <summary>The line ran, but no current impact-map evidence says which tests executed it.</summary>
    NoTestEvidence,

    /// <summary>The tests credited with executing the line include both passing and failing tests.</summary>
    MixedEvidence,

    /// <summary>A test credited with executing the line has no pass or fail result, or every credited test was skipped.</summary>
    TestsNotRun,

    /// <summary>
    /// Evidence recorded against an older version of the file may credit other tests with the line, and their
    /// outcomes would change the verdict.
    /// </summary>
    OutdatedEvidence,

    /// <summary>
    /// A run was publishing new coverage and test results while they were read, so the line's coverage and its
    /// tests' outcomes may come from different runs.
    /// </summary>
    ResultsUpdating,
}
