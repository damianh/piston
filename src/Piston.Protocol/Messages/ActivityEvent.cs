// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Nodes;

namespace Piston.Protocol.Messages;

/// <summary>Wire-level discriminated union for activity timeline events.</summary>
/// <param name="Id">Unique identity assigned by the daemon when the event is recorded, used to
/// de-duplicate replayed history against events already received live.</param>
public sealed record ActivityEvent(
    string Type,
    DateTimeOffset Timestamp,
    string? SolutionPath,
    JsonNode? Data,
    string? Id = null
);
