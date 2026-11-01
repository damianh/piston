// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>
/// Admission was cancelled while queued. <see cref="QueueSequence"/> lets a superseding run in the
/// same daemon keep the queue position instead of re-joining at the back; null when the run never
/// joined the queue.
/// </summary>
public sealed class ExecutionAdmissionCancelledException(long? queueSequence, CancellationToken token)
    : OperationCanceledException("Cancelled while queued for machine-wide execution admission.", token)
{
    public long? QueueSequence { get; } = queueSequence;
}
