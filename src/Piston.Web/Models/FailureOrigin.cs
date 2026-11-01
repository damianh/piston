// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>What the retained timeline establishes about where a failure came from.</summary>
public enum FailureOrigin
{
    /// <summary>A retained run is known to have started the current failing streak.</summary>
    Run,

    /// <summary>No run of the session changed the test from passing to failing: it failed in the session's first results.</summary>
    SessionStart,

    /// <summary>
    /// The retained data cannot say: the introducing run was dropped from the timeline, or a run's capped outcome
    /// lists may have omitted the test.
    /// </summary>
    Unknown,
}
