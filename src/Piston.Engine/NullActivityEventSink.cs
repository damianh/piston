// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine;

/// <summary>No-op sink used when no activity observer is registered.</summary>
internal sealed class NullActivityEventSink : IActivityEventSink
{
    public static readonly NullActivityEventSink Instance = new();

    public void Emit(EngineActivityEvent activity) { }
}
