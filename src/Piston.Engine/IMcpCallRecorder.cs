// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine;

/// <summary>
/// Abstraction for recording MCP tool call events.
/// Decouples MCP tools from protocol-layer types.
/// </summary>
public interface IMcpCallRecorder
{
    void Record(string toolName, string? paramsSummary, string? resultSummary, double durationMs, bool succeeded);
}
