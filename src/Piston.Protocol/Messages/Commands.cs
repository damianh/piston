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
