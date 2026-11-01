// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>How the Coverage tab groups measured files.</summary>
public enum CoverageGrouping
{
    /// <summary>By the assembly (Cobertura package) that contains the file.</summary>
    Module,

    /// <summary>By directory, as a tree.</summary>
    Folder,

    /// <summary>No grouping.</summary>
    Flat,
}
