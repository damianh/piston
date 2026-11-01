// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

public enum TestResourceWaitState
{
    /// <summary>Queued behind runs that hold or are ahead for a needed resource.</summary>
    Waiting,

    /// <summary>All needed resources are held by this run.</summary>
    Acquired,
}
