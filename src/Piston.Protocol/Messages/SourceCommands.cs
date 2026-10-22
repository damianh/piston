// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Request the text of a source file in the watched solution, e.g. one named by a stack frame.</summary>
/// <param name="FilePath">Absolute path, or a path relative to the solution directory.</param>
public sealed record GetSourceCommand(string FilePath);
