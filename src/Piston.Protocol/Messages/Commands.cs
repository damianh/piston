namespace Piston.Protocol.Messages;

public sealed record StartCommand(string SolutionPath);

public sealed record ForceRunCommand;

public sealed record StopCommand;

public sealed record SetFilterCommand(string? Filter);

public sealed record ClearResultsCommand;

/// <summary>Params payload for the start JSON-RPC command.</summary>
public sealed record StartCommandParams(string SolutionPath);

/// <summary>Params payload for the set-filter JSON-RPC command.</summary>
public sealed record SetFilterCommandParams(string? Filter);

/// <summary>Response for <c>diagnostics/getAll</c>.</summary>
public sealed record DiagnosticsResponse(IReadOnlyList<DiagnosticEntryData> Diagnostics);

/// <summary>Response for <c>mcp/getCallLog</c>.</summary>
public sealed record McpCallLogResponse(IReadOnlyList<McpToolCallData> Calls);
