// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Hosting;

/// <summary>The daemon could not start one of its endpoints (for example, a port is already in use).</summary>
public sealed class DaemonStartupException(string message, Exception? innerException = null)
    : Exception(message, innerException);
