// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Whether a run's results correspond to the source inputs present when it finished.</summary>
public enum ResultFreshness
{
    /// <summary>Freshness could not be established (fingerprint unavailable or run did not finish).</summary>
    Unknown,

    /// <summary>Source inputs were identical before and after execution and no change was observed.</summary>
    Current,

    /// <summary>Source inputs changed while the run was executing; results must not be treated as current.</summary>
    Stale,
}
