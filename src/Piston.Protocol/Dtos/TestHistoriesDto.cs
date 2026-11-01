// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>Recorded results of the tests matching a name.</summary>
/// <param name="Enabled">False when the daemon keeps no run history.</param>
public sealed record TestHistoriesDto(bool Enabled, IReadOnlyList<TestHistoryDto> Tests);
