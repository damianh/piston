// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Nodes;

namespace Piston.Protocol.JsonRpc;

/// <summary>JSON-RPC 2.0 error object.</summary>
public sealed record JsonRpcError(int Code, string Message, JsonNode? Data = null);
