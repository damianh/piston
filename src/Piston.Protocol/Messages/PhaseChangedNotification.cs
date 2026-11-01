// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Protocol.Messages;

public sealed record PhaseChangedNotification(
    PistonPhaseDto Phase,
    string? Detail
);
