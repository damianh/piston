using System.Text.Json.Nodes;
using Piston.Hosting.Services;
using Piston.Engine;
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
    private DiagnosticWatcherService? _diagnosticWatcher;
    private McpCallTracker? _mcpCallTracker;

    public EngineCommandDispatcher(IEngine engine) => _engine = engine;

    public void SetDiagnosticWatcher(DiagnosticWatcherService watcher) =>
        _diagnosticWatcher = watcher;

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

            case ProtocolMethods.CoverageGetForFile:
            {
                var cmd = JsonRpcSerializer.DeserializeParams<GetFileCoverageCommand>(@params);
                var filePath = cmd?.FilePath ?? string.Empty;
                var result = new FileCoverageDto(filePath, Array.Empty<CoverageLineDto>());
                return JsonNode.Parse(
                    System.Text.Json.JsonSerializer.Serialize(result, JsonRpcSerializer.Options));
            }

            case ProtocolMethods.DiagnosticsGetAll:
            {
                var diagnostics = _diagnosticWatcher?.CurrentDiagnostics ?? [];
                var response = new DiagnosticsResponse(diagnostics);
                return JsonNode.Parse(
                    System.Text.Json.JsonSerializer.Serialize(response, JsonRpcSerializer.Options));
            }

            case ProtocolMethods.McpGetCallLog:
            {
                var calls = _mcpCallTracker?.GetCallLog() ?? [];
                var response = new McpCallLogResponse(calls);
                return JsonNode.Parse(
                    System.Text.Json.JsonSerializer.Serialize(response, JsonRpcSerializer.Options));
            }

            default:
                throw new JsonRpcException(
                    JsonRpcErrorCodes.MethodNotFound,
                    $"Method not found: {method}");
        }
    }

    private static void ValidateFilter(string? filter)
    {
        if (filter is null)
            return;

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
