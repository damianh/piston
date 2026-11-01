// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Export the solution-wide coverage report.</summary>
/// <param name="Format"><c>lcov</c> or <c>cobertura</c>.</param>
/// <param name="ExportId">Snapshot identifier from the previous chunk; null starts a new export.</param>
/// <param name="Offset">Character offset requested by the previous chunk's <c>nextOffset</c>.</param>
public sealed record ExportCoverageCommand(string Format, string? ExportId = null, int Offset = 0);
