// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Hosting;

internal sealed record DashboardEndpoint(string State, string Message, string? Url = null,
    string? Worktree = null, int? ProcessId = null, DateTimeOffset? AcquiredAt = null);
