namespace Piston.Cli.Protocol;

/// <summary>
/// Thrown by <see cref="ICommandDispatcher"/> implementations to signal a JSON-RPC protocol error.
/// </summary>
internal sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
