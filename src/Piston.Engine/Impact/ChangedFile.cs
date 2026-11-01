// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Impact;

/// <summary>A changed source file to select tests for.</summary>
/// <param name="FilePath">Absolute path of the file.</param>
/// <param name="Content">Its current content, or null when it was deleted.</param>
internal sealed record ChangedFile(string FilePath, string? Content);
