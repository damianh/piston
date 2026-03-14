using Piston.Protocol.JsonRpc;

namespace Piston.Controller.Protocol;

/// <summary>
/// Thrown by <see cref="ICommandDispatcher"/> implementations to signal a JSON-RPC protocol error.
/// </summary>
internal sealed class JsonRpcException : Exception
{
    public int Code { get; }

    public JsonRpcException(int code, string message)
        : base(message)
    {
        Code = code;
    }
}
