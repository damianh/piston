// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>Formats the solution-wide coverage report can be exported in.</summary>
public enum CoverageExportFormat
{
    /// <summary>LCOV tracefile (<c>lcov.info</c>), read by most editors and coverage services.</summary>
    Lcov,

    /// <summary>Cobertura XML, read by CI systems and ReportGenerator.</summary>
    Cobertura,
}
