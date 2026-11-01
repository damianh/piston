// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Verification;

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
