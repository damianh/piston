// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Verification;

/// <summary>Evaluation of a terminal run against the present source fingerprint.</summary>
/// <param name="Verdict">Verdict for this run.</param>
/// <param name="Retryable">True when another attempt may produce a current result (source changed or run superseded).</param>
/// <param name="Reason">Explanation for non-passing verdicts.</param>
public sealed record VerificationEvaluation(VerificationVerdict Verdict, bool Retryable, string? Reason);
