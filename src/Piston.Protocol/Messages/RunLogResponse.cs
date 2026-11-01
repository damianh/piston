// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Protocol.Messages;

/// <summary>Response for <c>tests/getRunLog</c>; <see cref="Log"/> is null when none was recorded for the project.</summary>
public sealed record RunLogResponse(ExecutionLogDto? Log);
