// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Nodes;

namespace Piston.Protocol.JsonRpc;

/// <summary>JSON-RPC 2.0 notification message (server → client, no response expected).</summary>
public sealed record JsonRpcNotification(
    string Method,
    JsonNode? Params = null
)
{
    public string JsonRpc { get; } = "2.0";
}
