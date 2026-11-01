// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine;

/// <summary>No-op implementation for when call tracking is not configured.</summary>
public sealed class NullMcpCallRecorder : IMcpCallRecorder
{
    public static readonly NullMcpCallRecorder Instance = new();

    public void Record(string toolName, string? paramsSummary, string? resultSummary, double durationMs, bool succeeded)
    {
    }
}
