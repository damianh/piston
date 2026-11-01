// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>The column the coverage file list is ordered by.</summary>
public enum CoverageSort
{
    /// <summary>By group name and file path.</summary>
    Name,

    /// <summary>By line coverage; ascending puts the least covered first.</summary>
    Coverage,
}
