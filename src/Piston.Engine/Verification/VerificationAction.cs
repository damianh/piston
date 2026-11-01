// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Verification;

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
