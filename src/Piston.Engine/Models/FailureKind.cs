// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>How a failing test relates to its recorded history in this worktree.</summary>
public enum FailureKind
{
    /// <summary>No earlier pass or failure is recorded, so whether the failure is new is unknown.</summary>
    Unknown,

    /// <summary>The previous recorded result passed.</summary>
    New,

    /// <summary>The previous recorded result also failed.</summary>
    PreExisting,

    /// <summary>The test both passed and failed on the same source fingerprint within the recent history.</summary>
    Flaky,
}
