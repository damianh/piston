// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>An added line of a changed file.</summary>
/// <param name="FilePath">Absolute path of the file.</param>
/// <param name="Line">1-based line number in the changed file.</param>
public sealed record ChangeLineRef(string FilePath, int Line);
