// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Verification;

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
