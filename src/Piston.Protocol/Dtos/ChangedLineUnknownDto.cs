// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>Unknown changed lines that share a reason.</summary>
/// <param name="Reason">Why the lines are unknown, e.g. <c>stale</c> or <c>mixedEvidence</c>.</param>
/// <param name="Lines">The lines, as inclusive ranges.</param>
public sealed record ChangedLineUnknownDto(string Reason, IReadOnlyList<LineRangeDto> Lines);
