// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>What the Coverage tab shows: every measured file, or the diff of the changes.</summary>
public enum CoverageMode
{
    Files,
    Changes,
}
