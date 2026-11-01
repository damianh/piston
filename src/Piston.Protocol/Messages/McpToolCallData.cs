// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

public sealed record McpToolCallData(
    string ToolName,
    string? ParamsSummary,
    string? ResultSummary,
    double DurationMs,
    bool Succeeded
);
