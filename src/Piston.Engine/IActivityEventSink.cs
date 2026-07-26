using Piston.Protocol.Messages;

namespace Piston.Engine;

/// <summary>
/// Sink that subsystems call to publish activity timeline events.
/// The ProtocolRouter implements this to broadcast events to connected clients.
/// </summary>
public interface IActivityEventSink
{
    void Emit(ActivityEvent activity);
}
