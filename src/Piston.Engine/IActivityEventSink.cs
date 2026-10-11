// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine;

/// <summary>
/// Sink that engine subsystems call to publish activity timeline events.
/// Hosts map these events to their client wire formats.
/// </summary>
public interface IActivityEventSink
{
    void Emit(EngineActivityEvent activity);
}
