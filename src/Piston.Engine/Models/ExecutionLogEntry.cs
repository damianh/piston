// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>One timestamped line of an <see cref="ExecutionLog"/>.</summary>
public sealed record ExecutionLogEntry(DateTimeOffset Timestamp, string Category, string Message);
