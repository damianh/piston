// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Verification;

/// <summary>Outcome of a request for current full verification.</summary>
public enum VerificationVerdict
{
    /// <summary>A current, full-scope, unfiltered run for the present source passed every test.</summary>
    Passed,

    /// <summary>A current, full-scope, unfiltered run for the present source had failing tests.</summary>
    TestsFailed,

    /// <summary>The full build for the present source failed; no tests ran.</summary>
    BuildFailed,

    /// <summary>Tests ran for the present source, but results were missing, unfinished or duplicated.</summary>
    Incomplete,

    /// <summary>The source changed during or after the run (or freshness is unknown); the result is not current.</summary>
    Stale,

    /// <summary>The run was cancelled (superseded by further edits or engine shutdown).</summary>
    Cancelled,

    /// <summary>The pipeline failed unexpectedly.</summary>
    Faulted,

    /// <summary>No terminal result was available within the verification timeout.</summary>
    TimedOut,

    /// <summary>A test filter is active or evaluated build inputs are outside the source fingerprint.</summary>
    NotFullScope,
}
