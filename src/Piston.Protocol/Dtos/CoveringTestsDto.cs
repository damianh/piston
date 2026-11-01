// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>The tests recorded as executing a source file.</summary>
/// <param name="FilePath">Absolute path of the file.</param>
/// <param name="Tests">The tests, ordered by project then name. Empty when none were recorded.</param>
public sealed record CoveringTestsDto(string FilePath, IReadOnlyList<CoveringTestDto> Tests);
