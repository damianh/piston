// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Request coverage data for a single source file.</summary>
public sealed record GetFileCoverageCommand(string FilePath);
