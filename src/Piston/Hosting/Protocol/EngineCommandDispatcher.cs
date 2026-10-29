// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Engine;
using Piston.Engine.Coverage;
using Piston.Engine.Execution;
using Piston.Engine.Models;
using Piston.Hosting.Configuration;
using Piston.Hosting.Mapping;
using Piston.Hosting.Services;
using Piston.Mcp.Tools;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;

namespace Piston.Hosting.Protocol;

/// <summary>
/// Bridges JSON-RPC command dispatch to <see cref="IEngine"/> method calls.
/// </summary>
public sealed class EngineCommandDispatcher : ICommandDispatcher
{
    private static readonly char[] ForbiddenFilterChars = ['"', '&', '|', ';', '`', '$'];

    private readonly IEngine _engine;
    private McpCallTracker? _mcpCallTracker;
    private DaemonControl? _daemonControl;
    private ActivityHistory? _activityHistory;
    private UserSettingsStore? _userSettings;
    private Func<CancellationToken, Task<MachineExecutionOverview>>? _machineOverviewProvider;
    private (string Id, CoverageExportDto Export, int Offset)? _pendingCoverageExport;

    // Even JSON's worst-case six-byte character escaping stays well below the 4 MiB pipe frame limit.
    internal const int CoverageExportChunkChars = 64 * 1024;

    public EngineCommandDispatcher(IEngine engine) => _engine = engine;

    private string? SolutionDirectory =>
        _engine.State.SolutionPath is { } solutionPath ? Path.GetDirectoryName(Path.GetFullPath(solutionPath)) : null;

    /// <summary>Delay before a requested shutdown starts, so the reply reaches the client first.</summary>
    internal static TimeSpan ShutdownReplyGrace { get; set; } = TimeSpan.FromMilliseconds(100);

    public void SetDaemonControl(DaemonControl control) =>
        _daemonControl = control;

    public void SetMcpCallTracker(McpCallTracker tracker) =>
        _mcpCallTracker = tracker;

    public void SetActivityHistory(ActivityHistory history) =>
        _activityHistory = history;

    public void SetUserSettingsStore(UserSettingsStore store) =>
        _userSettings = store;

    public void SetMachineOverviewProvider(Func<CancellationToken, Task<MachineExecutionOverview>> provider) =>
        _machineOverviewProvider = provider;

    public async Task<JsonNode?> HandleCommandAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        switch (method)
        {
            case ProtocolMethods.EngineStart:
                // Security: reject in headless mode — solution is configured at launch.
                // Accepting arbitrary paths is a command injection risk.
                throw new JsonRpcException(
                    JsonRpcErrorCodes.InvalidRequest,
                    "engine/start not available in headless mode — solution is configured at launch.");

            case ProtocolMethods.EngineForceRun:
                await _engine.ForceRunAsync().ConfigureAwait(false);
                return null;

            case ProtocolMethods.EngineStop:
                _engine.Stop();
                return null;

            case ProtocolMethods.EngineSetFilter:
                {
                    var cmd = JsonRpcSerializer.DeserializeParams<SetFilterCommand>(@params);
                    var filter = cmd?.Filter;
                    ValidateFilter(filter);
                    _engine.SetFilter(filter);
                    return null;
                }

            case ProtocolMethods.EngineClearResults:
                _engine.ClearResults();
                return null;

            case ProtocolMethods.EngineReset:
                await _engine.ResetAsync().ConfigureAwait(false);
                return null;

            case ProtocolMethods.CoverageGetForFile:
                {
                    var cmd = JsonRpcSerializer.DeserializeParams<GetFileCoverageCommand>(@params);
                    var filePath = cmd?.FilePath ?? string.Empty;
                    var coverage = string.IsNullOrWhiteSpace(filePath) ? null : _engine.GetFileCoverage(filePath);
                    var result = DtoMapper.ToFileCoverageDto(
                        filePath, coverage, SolutionDirectory, cmd?.IncludeSource ?? false);
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(result, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.CoverageGetCoveringTests:
                {
                    var cmd = JsonRpcSerializer.DeserializeParams<GetCoveringTestsCommand>(@params);
                    var filePath = cmd?.FilePath ?? string.Empty;
                    var tests = string.IsNullOrWhiteSpace(filePath) ? null : _engine.GetCoveringTests(filePath);
                    var result = DtoMapper.ToCoveringTestsDto(filePath, tests);
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(result, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.CoverageGetChangedLines:
                {
                    GetChangedLineCoverageCommand? cmd;
                    try
                    {
                        cmd = JsonRpcSerializer.DeserializeParams<GetChangedLineCoverageCommand>(@params);
                    }
                    catch (JsonException ex)
                    {
                        throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"Invalid parameters: {ex.Message}");
                    }

                    ChangedLineCoverageDto result;
                    // The flag, not the report: reading the report waits on the lock a run holds while storing coverage.
                    if (!_engine.State.CoverageEnabled)
                    {
                        result = DtoMapper.DisabledChangedLineCoverage;
                    }
                    else
                    {
                        var solutionDirectory = SolutionDirectory
                            ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidRequest, "No solution is loaded.");
                        try
                        {
                            result = (await ChangedLineVerdicts
                                .GetAsync(_engine, solutionDirectory, cmd?.Base, cmd?.BaseRef, ct)
                                .ConfigureAwait(false)).ToDto(solutionDirectory);
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                        {
                            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, ex.Message);
                        }
                    }

                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(result, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.SourceGet:
                {
                    var cmd = JsonRpcSerializer.DeserializeParams<GetSourceCommand>(@params);
                    var result = SourceFileReader.Read(cmd?.FilePath, SolutionDirectory);
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(result, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.HistoryGetRuns:
                {
                    var runs = _engine.GetStoredRuns();
                    var result = new StoredRunsDto(runs is not null, runs?.Select(DtoMapper.ToDto).ToList() ?? []);
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(result, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.HistoryGetRun:
                {
                    var cmd = JsonRpcSerializer.DeserializeParams<GetStoredRunCommand>(@params);
                    var run = string.IsNullOrWhiteSpace(cmd?.RunId) ? null : _engine.GetStoredRun(cmd.RunId);
                    return run is null
                        ? null
                        : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(run.ToDto(), JsonRpcSerializer.Options));
                }

            case ProtocolMethods.HistoryGetTestMarks:
                {
                    var failures = _engine.GetLatestFailures();
                    var flaky = _engine.GetFlakyTests();
                    var result = new TestMarksDto(failures is not null || flaky is not null, DtoMapper.ToMarks(failures ?? [], flaky ?? []));
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(result, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.HistoryGetTestHistory:
                {
                    var cmd = JsonRpcSerializer.DeserializeParams<GetTestHistoryCommand>(@params);
                    // An empty name matches no test, but still reports whether history is on.
                    IReadOnlyList<TestHistory>? histories;
                    try
                    {
                        histories = _engine.GetTestHistory(cmd?.Test ?? "", cmd?.ProjectId);
                    }
                    catch (ArgumentException ex)
                    {
                        throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, ex.Message);
                    }
                    var result = new TestHistoriesDto(histories is not null, histories?.Select(DtoMapper.ToDto).ToList() ?? []);
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(result, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.SessionGetTimeline:
                {
                    var cmd = JsonRpcSerializer.DeserializeParams<GetSessionTimelineCommand>(@params);
                    var stored = _engine.GetStoredRuns()?.Select(r => r.RunId).ToHashSet(StringComparer.Ordinal);
                    var result = _engine.State.ToSessionTimelineDto(cmd?.AfterSequence, id => stored?.Contains(id) == true);
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(result, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.CoverageGetSummary:
                {
                    var result = DtoMapper.ToCoverageSummaryDto(_engine.GetCoverageReport(), SolutionDirectory);
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(result, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.CoverageExport:
                {
                    var cmd = JsonRpcSerializer.DeserializeParams<ExportCoverageCommand>(@params);
                    if (cmd is null || !CoverageExporter.TryParseFormat(cmd.Format, out var format))
                    {
                        throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "format must be 'lcov' or 'cobertura'.");
                    }

                    var result = ExportCoverageChunk(cmd, format);
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(result, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.McpGetCallLog:
                {
                    var calls = _mcpCallTracker?.GetCallLog() ?? [];
                    var response = new McpCallLogResponse(calls);
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(response, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.ActivityGetLog:
                {
                    var response = new ActivityLogResponse(_activityHistory?.Snapshot() ?? []);
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(response, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.EngineRunTests:
            case ProtocolMethods.EngineRunFailed:
                {
                    var cmd = method == ProtocolMethods.EngineRunFailed
                        ? RunTestsCommand.ForFailed(_engine.State.ToSnapshot().Suites)
                            ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidRequest, "No failed tests to run.")
                        : JsonRpcSerializer.DeserializeParams<RunTestsCommand>(@params);
                    if (cmd?.ProjectIds is not { Count: > 0 } projectIds || projectIds.Any(string.IsNullOrWhiteSpace))
                    {
                        throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "projects is required.");
                    }

                    // Clients follow the run through state notifications; only a rejection is reported here.
                    Task run;
                    try
                    {
                        run = _engine.RunSelectedAsync(new Engine.Models.TestSelection(projectIds, cmd.Tests));
                        if (run.IsFaulted)
                        {
                            await run.ConfigureAwait(false);
                        }
                    }
                    catch (ArgumentException ex)
                    {
                        throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, ex.Message);
                    }
                    catch (InvalidOperationException ex)
                    {
                        throw new JsonRpcException(JsonRpcErrorCodes.InvalidRequest, ex.Message);
                    }

                    _ = run.ContinueWith(
                        static t => _ = t.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return null;
                }

            case ProtocolMethods.TestsGetRunLog:
                {
                    var cmd = JsonRpcSerializer.DeserializeParams<GetRunLogCommand>(@params);
                    if (string.IsNullOrWhiteSpace(cmd?.ProjectId))
                    {
                        throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "project is required.");
                    }

                    var response = new RunLogResponse(_engine.State.FindRunLog(cmd.ProjectId));
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(response, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.DaemonInfo:
                {
                    var control = RequireDaemonControl(method);
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(control.GetInfo(), JsonRpcSerializer.Options));
                }

            case ProtocolMethods.DaemonShutdown:
                {
                    var control = RequireDaemonControl(method);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(ShutdownReplyGrace, CancellationToken.None).ConfigureAwait(false);
                        control.RequestShutdown();
                    }, CancellationToken.None);
                    return null;
                }

            case ProtocolMethods.SettingsGet:
                {
                    var store = RequireUserSettings(method);
                    return SerializeSettings(store, store.Get());
                }

            case ProtocolMethods.MachineGetOverview:
                {
                    var provider = _machineOverviewProvider
                        ?? throw new JsonRpcException(JsonRpcErrorCodes.MethodNotFound, $"Method not available in this host: {method}");
                    var overview = await provider(ct).ConfigureAwait(false);
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(overview.ToDto(), JsonRpcSerializer.Options));
                }

            case ProtocolMethods.SettingsUpdate:
                {
                    var store = RequireUserSettings(method);
                    UserSettings? patch;
                    try
                    {
                        patch = JsonRpcSerializer.DeserializeParams<UserSettings>(@params);
                    }
                    catch (JsonException ex)
                    {
                        throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"Invalid settings: {ex.Message}");
                    }

                    try
                    {
                        return SerializeSettings(store, store.Update(patch ?? new UserSettings()));
                    }
                    catch (ArgumentException ex)
                    {
                        throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"Invalid settings: {ex.Message}");
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                        throw new JsonRpcException(JsonRpcErrorCodes.InternalError, ex.Message);
                    }
                }

            default:
                throw new JsonRpcException(
                    JsonRpcErrorCodes.MethodNotFound,
                    $"Method not found: {method}");
        }
    }

    private CoverageExportDto ExportCoverageChunk(ExportCoverageCommand command, CoverageExportFormat format)
    {
        CoverageExportDto snapshot;
        var id = command.ExportId;
        if (id is null)
        {
            if (command.Offset != 0)
            {
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "A new export must start at offset zero.");
            }

            _pendingCoverageExport = null;
            var export = CoverageExporter.Export(_engine, format)
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidRequest, "Coverage is disabled.");
            snapshot = new CoverageExportDto(
                format.ToString().ToLowerInvariant(), CoverageExporter.FileName(format),
                export.Content, export.FileCount, export.StaleFilesSkipped);
        }
        else if (_pendingCoverageExport is { } pending
            && pending.Id == id && pending.Offset == command.Offset
            && pending.Export.Format == format.ToString().ToLowerInvariant())
        {
            snapshot = pending.Export;
        }
        else
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Unknown export or invalid continuation offset or format.");
        }

        var next = (int)Math.Min((long)command.Offset + CoverageExportChunkChars, snapshot.Content.Length);
        if (next < snapshot.Content.Length && char.IsHighSurrogate(snapshot.Content[next - 1]))
        {
            next--;
        }

        var content = snapshot.Content[command.Offset..next];
        if (next < snapshot.Content.Length)
        {
            id ??= Guid.NewGuid().ToString("N");
            _pendingCoverageExport = (id, snapshot, next);
            return snapshot with { Content = content, ExportId = id, NextOffset = next };
        }

        _pendingCoverageExport = null;
        return snapshot with { Content = content, ExportId = id };
    }

    private DaemonControl RequireDaemonControl(string method) =>
        _daemonControl ?? throw new JsonRpcException(
            JsonRpcErrorCodes.MethodNotFound, $"Method not available in this host: {method}");

    private UserSettingsStore RequireUserSettings(string method) =>
        _userSettings ?? throw new JsonRpcException(
            JsonRpcErrorCodes.MethodNotFound, $"Method not available in this host: {method}");

    private static JsonNode? SerializeSettings(UserSettingsStore store, UserSettings settings) =>
        JsonSerializer.SerializeToNode(new UserSettingsResponse(settings, store.FilePath), JsonRpcSerializer.Options);

    private static void ValidateFilter(string? filter)
    {
        if (filter is null)
        {
            return;
        }

        foreach (var ch in ForbiddenFilterChars)
        {
            if (filter.Contains(ch))
            {
                throw new JsonRpcException(
                    JsonRpcErrorCodes.InvalidParams,
                    $"Filter contains forbidden character '{ch}'. Shell metacharacters are not allowed.");
            }
        }
    }
}
