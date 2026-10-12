// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Messages;

namespace Piston.Hosting.Protocol;

/// <summary>Process-level control exposed to protocol clients by a hosting daemon.</summary>
/// <param name="GetInfo">Describes the daemon process.</param>
/// <param name="RequestShutdown">Requests that the daemon stops and exits; must not block.</param>
public sealed record DaemonControl(Func<DaemonInfoResponse> GetInfo, Action RequestShutdown);
