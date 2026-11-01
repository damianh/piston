// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Response for <c>activity/getLog</c>: retained activity events, oldest first.</summary>
public sealed record ActivityLogResponse(IReadOnlyList<ActivityEvent> Events);
