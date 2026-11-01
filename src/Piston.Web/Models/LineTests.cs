// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>The tests recorded as executing a changed line.</summary>
/// <param name="Credited">Tests measured on the file as it is now: they ran the line, and their outcome counts.</param>
/// <param name="Outdated">Tests measured on an older version of the file, whose ranges include the line number but may not be the line.</param>
public sealed record LineTests(IReadOnlyList<CoveringTestDto> Credited, IReadOnlyList<CoveringTestDto> Outdated);
