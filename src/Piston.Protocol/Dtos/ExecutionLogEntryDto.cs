// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>One line of an <see cref="ExecutionLogDto"/>.</summary>
public sealed record ExecutionLogEntryDto(DateTimeOffset Timestamp, string Category, string Message);
