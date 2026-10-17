// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>One line of an <see cref="ExecutionLogDto"/>.</summary>
public sealed record ExecutionLogEntryDto(DateTimeOffset Timestamp, string Category, string Message);

/// <summary>
/// What Piston did to run the test process that produced a result: runner, command, fallbacks, the
/// process's console output, exit code and result summary.
/// </summary>
/// <param name="ProjectPath">The test project the process ran.</param>
/// <param name="Runner">How the tests were run: <c>VSTest</c>, <c>MTP server</c> or <c>MTP dotnet test</c>.</param>
/// <param name="ExitCode">Exit code of the process, when it exited on its own and it is known.</param>
/// <param name="StartedAt">When Piston started running the project.</param>
/// <param name="CompletedAt">When the process finished.</param>
/// <param name="OmittedEntries">Entries dropped from the middle of a long log.</param>
/// <param name="Entries">Retained entries, oldest first.</param>
/// <param name="RunId">The run that produced the result, when known.</param>
public sealed record ExecutionLogDto(
    string ProjectPath,
    string? Runner,
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int OmittedEntries,
    IReadOnlyList<ExecutionLogEntryDto> Entries,
    string? RunId);
