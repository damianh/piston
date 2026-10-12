// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

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

/// <summary>How the run behind a verification result was obtained.</summary>
public enum VerificationSource
{
    /// <summary>No run was used.</summary>
    None,

    /// <summary>An existing terminal run for the present source was reused without building.</summary>
    Reused,

    /// <summary>A full-scope run that was already in progress was awaited.</summary>
    AwaitedActive,

    /// <summary>A new forced full run was requested.</summary>
    Forced,
}

/// <summary>Result of a current-full-verification request.</summary>
/// <param name="Verdict">The verdict; only <see cref="VerificationVerdict.Passed"/> is proof that tests pass for the present source.</param>
/// <param name="Reason">Human-readable explanation for non-passing verdicts.</param>
/// <param name="Source">How <paramref name="Run"/> was obtained.</param>
/// <param name="Attempts">Number of runs reused, awaited or requested.</param>
/// <param name="Run">The run the verdict is based on, if any.</param>
/// <param name="SourceFingerprint">The source fingerprint captured when the verdict was decided.</param>
public sealed record VerificationResult(
    VerificationVerdict Verdict,
    string? Reason,
    VerificationSource Source,
    int Attempts,
    TestRunRecord? Run,
    SourceFingerprint? SourceFingerprint);

/// <summary>Next action decided by <see cref="VerificationPolicy.Decide"/>.</summary>
public enum VerificationAction
{
    /// <summary>Reuse <see cref="VerificationDecision.Run"/>.</summary>
    Reuse,

    /// <summary>Await the active run <see cref="VerificationDecision.Run"/>.</summary>
    AwaitActive,

    /// <summary>Request a new forced full run.</summary>
    RequestForced,

    /// <summary>Stop with <see cref="VerificationDecision.Verdict"/>.</summary>
    Final,
}

/// <summary>A verification step decision.</summary>
public sealed record VerificationDecision(
    VerificationAction Action,
    TestRunRecord? Run = null,
    VerificationVerdict? Verdict = null,
    string? Reason = null);

/// <summary>Evaluation of a terminal run against the present source fingerprint.</summary>
/// <param name="Verdict">Verdict for this run.</param>
/// <param name="Retryable">True when another attempt may produce a current result (source changed or run superseded).</param>
/// <param name="Reason">Explanation for non-passing verdicts.</param>
public sealed record VerificationEvaluation(VerificationVerdict Verdict, bool Retryable, string? Reason);
