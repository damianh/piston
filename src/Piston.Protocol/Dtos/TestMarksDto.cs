// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>History marks for the tests that have any.</summary>
/// <param name="Enabled">False when the daemon keeps no run history.</param>
public sealed record TestMarksDto(bool Enabled, IReadOnlyList<TestMarkDto> Tests);
