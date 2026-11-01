// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Response for <c>mcp/getCallLog</c>.</summary>
public sealed record McpCallLogResponse(IReadOnlyList<McpToolCallData> Calls);
