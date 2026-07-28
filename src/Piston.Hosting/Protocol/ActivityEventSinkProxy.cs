using Piston.Engine;
using Piston.Protocol.Messages;

namespace Piston.Hosting.Protocol;

/// <summary>
/// Proxy that forwards activity events to an inner sink once it is set.
/// Allows PistonEngine (created before ProtocolRouter) to emit activity events
/// that are routed to the router after it is constructed.
/// </summary>
public sealed class ActivityEventSinkProxy : IActivityEventSink
{
    private volatile IActivityEventSink? _inner;

    public void SetSink(IActivityEventSink sink) => _inner = sink;

    public void Emit(ActivityEvent activity) => _inner?.Emit(activity);
}
