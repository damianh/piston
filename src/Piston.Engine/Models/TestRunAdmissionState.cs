// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Machine-wide admission state of a run.</summary>
public enum TestRunAdmissionState
{
    /// <summary>Waiting in the machine-wide FIFO queue.</summary>
    Queued,

    /// <summary>Admitted; the run's reservation counts against the machine budget until it ends.</summary>
    Admitted,
}
