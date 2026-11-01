// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>
/// Machine-wide execution coordination is unavailable, misconfigured or cannot admit work.
/// Piston reports this explicitly instead of running without ownership or budgeting.
/// </summary>
public class ExecutionCoordinationException : Exception
{
    public ExecutionCoordinationException(string message) : base(message) { }

    public ExecutionCoordinationException(string message, Exception innerException) : base(message, innerException) { }
}
