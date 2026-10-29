// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

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

/// <summary>Parameters for <c>engine/runTests</c>: the tests to run.</summary>
/// <param name="ProjectIds">Solution-relative IDs of the test projects to run, as reported in <see cref="Dtos.TestSuiteDto.ProjectId"/>.</param>
/// <param name="Tests">
/// Fully qualified names of the tests to run within those projects, as reported in
/// <see cref="Dtos.TestResultDto.FullyQualifiedName"/>; null runs every test in them. All cases of a
/// parameterised test run. A name that, less its argument list, contains a double quote is rejected.
/// </param>
public sealed record RunTestsCommand(IReadOnlyList<string> ProjectIds, IReadOnlyList<string>? Tests = null)
{
    public static RunTestsCommand? ForFailed(IReadOnlyList<TestSuiteDto> suites)
    {
        var failed = suites
            .SelectMany(s => s.Tests.Where(t => t.Status == TestStatusDto.Failed).Select(t => (Project: s.ProjectId ?? s.Name, Test: t)))
            .ToList();
        return failed.Count == 0 ? null : new RunTestsCommand(
            [.. failed.Select(f => f.Project).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. failed.Select(f => f.Test.FullyQualifiedName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }
}

/// <summary>Parameters for <c>tests/getRunLog</c>: the test project whose latest run log to fetch.</summary>
/// <param name="ProjectId">The solution-relative project ID, as reported in <see cref="Dtos.TestSuiteDto.ProjectId"/>.</param>
public sealed record GetRunLogCommand(string ProjectId);

/// <summary>Response for <c>tests/getRunLog</c>; <see cref="Log"/> is null when none was recorded for the project.</summary>
public sealed record RunLogResponse(ExecutionLogDto? Log);

/// <summary>Response for <c>mcp/getCallLog</c>.</summary>
public sealed record McpCallLogResponse(IReadOnlyList<McpToolCallData> Calls);

/// <summary>Response for <c>activity/getLog</c>: retained activity events, oldest first.</summary>
public sealed record ActivityLogResponse(IReadOnlyList<ActivityEvent> Events);

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
