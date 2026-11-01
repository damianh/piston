// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Which test projects a run selected.</summary>
public enum TestRunScope
{
    /// <summary>All test projects in the solution.</summary>
    Full,

    /// <summary>Only the test projects affected by a change.</summary>
    Selective,
}
