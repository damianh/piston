// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Execution;

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
