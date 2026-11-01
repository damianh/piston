// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>The runs kept in the run history, newest first.</summary>
/// <param name="Enabled">False when the daemon keeps no run history.</param>
public sealed record StoredRunsDto(bool Enabled, IReadOnlyList<StoredRunSummaryDto> Runs);
