// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Engine;
using Piston.Engine.Coverage;
using Piston.Hosting.Configuration;
using Piston.Hosting.Mapping;
using Piston.Hosting.Services;
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

            case ProtocolMethods.CoverageGetSummary:
                {
                    var result = DtoMapper.ToCoverageSummaryDto(_engine.GetCoverageReport(), SolutionDirectory);
                    return JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(result, JsonRpcSerializer.Options));
                }

            case ProtocolMethods.CoverageExport:
                {
                    var cmd = JsonRpcSerializer.DeserializeParams<ExportCoverageCommand>(@params);
                    if (!CoverageExporter.TryParseFormat(cmd?.Format, out var format))
                    {
                        throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "format must be 'lcov' or 'cobertura'.");
                    }

                    var export = CoverageExporter.Export(_engine, format)
                        ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidRequest, "Coverage is disabled.");
                    var result = new CoverageExportDto(
                        format.ToString().ToLowerInvariant(),
                        CoverageExporter.FileName(format),
                        export.Content,
                        export.FileCount,
                        export.StaleFilesSkipped);
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
                {
                    var cmd = JsonRpcSerializer.DeserializeParams<RunTestsCommand>(@params);
                    if (cmd?.Projects is not { Count: > 0 } projects || projects.Any(string.IsNullOrWhiteSpace))
                    {
                        throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "projects is required.");
                    }

                    // Clients follow the run through state notifications; only a rejection is reported here.
                    Task run;
                    try
                    {
                        run = _engine.RunSelectedAsync(new Engine.Models.TestSelection(projects, cmd.Tests));
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
                    if (string.IsNullOrWhiteSpace(cmd?.Project))
                    {
                        throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "project is required.");
                    }

                    var response = new RunLogResponse(_engine.State.FindRunLog(cmd.Project));
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
