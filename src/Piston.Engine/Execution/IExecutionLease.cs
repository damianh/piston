// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Execution;

/// <summary>An admitted run's reservation; disposing releases it.</summary>
internal interface IExecutionLease : IDisposable
{
    /// <summary>Admission details; null when the engine runs without machine coordination.</summary>
    TestRunAdmission? Status { get; }
}
