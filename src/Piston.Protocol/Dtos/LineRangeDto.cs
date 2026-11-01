// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>An inclusive, 1-based range of source lines.</summary>
public sealed record LineRangeDto(int Start, int End);
