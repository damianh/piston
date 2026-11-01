// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Verification;

/// <summary>A verification step decision.</summary>
public sealed record VerificationDecision(
    VerificationAction Action,
    TestRunRecord? Run = null,
    VerificationVerdict? Verdict = null,
    string? Reason = null);
