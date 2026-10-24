// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>The test that has been running longest in the current run.</summary>
/// <param name="Name">The test's result key.</param>
/// <param name="ProjectPath">The test project running it.</param>
/// <param name="StartedAt">When the daemon first saw it running.</param>
/// <param name="PossiblyHanging">True once it has run longer than the hang threshold.</param>
public sealed record RunningTestDto(string Name, string ProjectPath, DateTimeOffset StartedAt, bool PossiblyHanging);
