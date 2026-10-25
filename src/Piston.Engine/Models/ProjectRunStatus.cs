// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

public enum ProjectRunStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Crashed,
    TimedOut,

    /// <summary>Queued for exclusive test resources (ports, containers…) held by another run on the machine.</summary>
    WaitingForResources,
}
