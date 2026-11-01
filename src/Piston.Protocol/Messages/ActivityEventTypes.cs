// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Discriminator constants for <see cref="ActivityEvent.Type"/>.</summary>
public static class ActivityEventTypes
{
    public const string SolutionLoaded = "solution.loaded";
    public const string FileChangesDetected = "file.changes";
    public const string BuildCompleted = "build.completed";
    public const string TestRunCompleted = "tests.completed";
    public const string McpToolCall = "mcp.toolCall";
    public const string Reconnect = "reconnect";
}
