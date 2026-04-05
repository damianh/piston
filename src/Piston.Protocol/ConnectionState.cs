namespace Piston.Protocol;

/// <summary>Represents the transport connection state of an engine client.</summary>
public enum ConnectionState
{
    /// <summary>The client is connected and ready to send/receive messages.</summary>
    Connected,

    /// <summary>The connection has been lost and reconnection has not yet started.</summary>
    Disconnected,

    /// <summary>The client is actively attempting to reconnect with exponential backoff.</summary>
    Reconnecting,
}
