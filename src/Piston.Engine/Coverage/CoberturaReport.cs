// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>
/// Parsed representation of a Cobertura XML coverage report.
/// </summary>
internal sealed record CoberturaReport(IReadOnlyList<FileCoverage> Files);
