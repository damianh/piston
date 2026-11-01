// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Response to <c>daemon/info</c>.</summary>
/// <param name="ProcessId">Daemon process ID.</param>
/// <param name="SolutionPath">Solution loaded by the daemon.</param>
/// <param name="PipeName">Named pipe for local IPC.</param>
/// <param name="StartedAt">When the daemon started.</param>
/// <param name="Version">Piston version, when known.</param>
/// <param name="WebUrl">Web dashboard URL.</param>
/// <param name="McpUrl">HTTP MCP endpoint URL; null when MCP is disabled.</param>
public sealed record DaemonInfoResponse(
    int ProcessId,
    string SolutionPath,
    string PipeName,
    DateTimeOffset StartedAt,
    string? Version,
    string WebUrl,
    string? McpUrl);
