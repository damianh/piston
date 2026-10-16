// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Nodes;
using Piston.Engine;
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

    public EngineCommandDispatcher(IEngine engine) => _engine = engine;

    /// <summary>Delay before a requested shutdown starts, so the reply reaches the client first.</summary>
    internal static TimeSpan ShutdownReplyGrace { get; set; } = TimeSpan.FromMilliseconds(100);

    public void SetDaemonControl(DaemonControl control) =>
        _daemonControl = control;

    public void SetMcpCallTracker(McpCallTracker tracker) =>
        _mcpCallTracker = tracker;

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
                    var result = new FileCoverageDto(filePath, Array.Empty<CoverageLineDto>());
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

            default:
                throw new JsonRpcException(
                    JsonRpcErrorCodes.MethodNotFound,
                    $"Method not found: {method}");
        }
    }

    private DaemonControl RequireDaemonControl(string method) =>
        _daemonControl ?? throw new JsonRpcException(
            JsonRpcErrorCodes.MethodNotFound, $"Method not available in this host: {method}");

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
