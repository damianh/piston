// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Params payload for the start JSON-RPC command.</summary>
public sealed record StartCommandParams(string SolutionPath);
