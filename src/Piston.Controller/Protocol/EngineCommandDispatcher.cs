using System.Text.Json.Nodes;
using Piston.Engine;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;

namespace Piston.Controller.Protocol;

/// <summary>
/// Bridges JSON-RPC command dispatch to <see cref="IEngine"/> method calls.
/// </summary>
internal sealed class EngineCommandDispatcher : ICommandDispatcher
{
    private static readonly char[] ForbiddenFilterChars = ['"', '&', '|', ';', '`', '$'];

    private readonly IEngine _engine;

    public EngineCommandDispatcher(IEngine engine)
    {
        _engine = engine;
    }

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
                // The engine's coverage store does not yet expose a per-file query API.
                // Return an empty result for now; this will be populated in a future phase
                // when the engine exposes coverage data via IEngine.
                var cmd = JsonRpcSerializer.DeserializeParams<GetFileCoverageCommand>(@params);
                var filePath = cmd?.FilePath ?? string.Empty;
                var result = new FileCoverageDto(filePath, Array.Empty<CoverageLineDto>());
                return JsonNode.Parse(
                    System.Text.Json.JsonSerializer.Serialize(result, JsonRpcSerializer.Options));
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
