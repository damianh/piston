// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Request the tests recorded as executing a source file.</summary>
/// <param name="FilePath">Absolute path, or a path relative to the solution directory.</param>
public sealed record GetCoveringTestsCommand(string FilePath);
