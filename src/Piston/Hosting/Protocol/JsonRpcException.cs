// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Hosting.Protocol;

/// <summary>
/// Thrown by <see cref="ICommandDispatcher"/> implementations to signal a JSON-RPC protocol error.
/// </summary>
public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
