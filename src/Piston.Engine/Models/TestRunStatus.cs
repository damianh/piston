// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Lifecycle state of a single test run.</summary>
public enum TestRunStatus
{
    /// <summary>
    /// Requested but not started: waiting for this daemon's previous run to finish, or (when
    /// <see cref="TestRunRecord.Admission"/> is set) queued for machine-wide execution admission.
    /// </summary>
    Queued,

    /// <summary>Analyzing, building or testing.</summary>
    Running,

    /// <summary>Every selected test project reported results.</summary>
    Completed,

    /// <summary>Tests ran, but at least one selected project crashed, failed to load or produced no results.</summary>
    Incomplete,

    /// <summary>The build failed; no tests ran.</summary>
    BuildFailed,

    /// <summary>Cancelled or superseded by a newer run before completing.</summary>
    Cancelled,

    /// <summary>The pipeline threw an unexpected exception.</summary>
    Faulted,
}
