// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

public enum TestResourceBlockerKind
{
    /// <summary>A running test project holds the resource.</summary>
    Held,

    /// <summary>A test project queued earlier needs the resource and goes first.</summary>
    Queued,
}
