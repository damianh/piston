// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Execution;

/// <summary>Admits runs for execution. Distinct from the per-daemon run lock that serializes a daemon's own runs.</summary>
internal interface IExecutionAdmission
{
    /// <summary>
    /// Waits until the run may execute. <paramref name="onStatus"/> is invoked when the queued status changes.
    /// </summary>
    /// <param name="inheritedSequence">Queue position of a superseded run of this daemon to keep.</param>
    /// <exception cref="ExecutionAdmissionCancelledException">Cancelled while queued.</exception>
    /// <exception cref="ExecutionCoordinationException">Coordination failed or the budget became invalid.</exception>
    Task<IExecutionLease> AdmitAsync(
        string runId, Action<TestRunAdmission> onStatus, long? inheritedSequence, CancellationToken ct);
}

/// <summary>An admitted run's reservation; disposing releases it.</summary>
internal interface IExecutionLease : IDisposable
{
    /// <summary>Admission details; null when the engine runs without machine coordination.</summary>
    TestRunAdmission? Status { get; }
}

/// <summary>Admits immediately with no machine coordination (unit tests and embedded use).</summary>
internal sealed class UnboundedExecutionAdmission : IExecutionAdmission
{
    public static readonly UnboundedExecutionAdmission Instance = new();

    public Task<IExecutionLease> AdmitAsync(
        string runId, Action<TestRunAdmission> onStatus, long? inheritedSequence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IExecutionLease>(Lease.Instance);
    }

    private sealed class Lease : IExecutionLease
    {
        public static readonly Lease Instance = new();
        public TestRunAdmission? Status => null;
        public void Dispose() { }
    }
}
