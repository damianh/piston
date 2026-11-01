// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>Whether an ownership lock is held by a live process.</summary>
public enum OwnershipLiveness
{
    /// <summary>No lock file.</summary>
    None,

    /// <summary>The lock is held: <see cref="OwnershipRecord.Owner"/> describes a running process.</summary>
    Live,

    /// <summary>The lock is free but owner details remain (the owner crashed or was killed).</summary>
    Stale,
}
