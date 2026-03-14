using System.Text.Json.Nodes;

namespace Piston.Controller.Protocol;

/// <summary>
/// Abstracts dispatching of JSON-RPC commands to the engine.
/// Used by <see cref="ClientSession"/> to decouple session I/O from engine concerns.
/// </summary>
internal interface ICommandDispatcher
{
    /// <summary>
    /// Dispatches a command by <paramref name="method"/> name with optional typed <paramref name="params"/>.
    /// Returns a result payload (may be null for void commands).
    /// Throws <see cref="JsonRpcException"/> for unknown methods or invalid requests.
    /// </summary>
    Task<JsonNode?> HandleCommandAsync(string method, JsonNode? @params, CancellationToken ct);
}
