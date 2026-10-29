// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Protocol.JsonRpc;

namespace Piston.Protocol.Messages;

/// <summary>Factory helpers for building <see cref="ActivityEvent"/> instances.</summary>
public static class ActivityEventFactory
{
    public static ActivityEvent SolutionLoaded(string solutionPath, int projectCount) =>
        Build(ActivityEventTypes.SolutionLoaded, solutionPath,
            new SolutionLoadedData(solutionPath, projectCount));

    public static ActivityEvent FileChangesDetected(string? solutionPath, IReadOnlyList<string> files) =>
        Build(ActivityEventTypes.FileChangesDetected, solutionPath,
            new FileChangesDetectedData(files, files.Count));

    public static ActivityEvent BuildCompleted(
        string? solutionPath, bool succeeded, double durationMs, IReadOnlyList<string> errors, int? errorCount = null) =>
        Build(ActivityEventTypes.BuildCompleted, solutionPath,
            new BuildCompletedData(succeeded, durationMs, errorCount ?? errors.Count, errors));

    public static ActivityEvent TestRunCompleted(string? solutionPath, int passed, int failed, int skipped, double durationMs, IReadOnlyList<TestFailureData> failures) =>
        Build(ActivityEventTypes.TestRunCompleted, solutionPath,
            new TestRunCompletedData(passed, failed, skipped, durationMs, failures));

    public static ActivityEvent McpToolCall(string? solutionPath, string toolName, string? paramsSummary, string? resultSummary, double durationMs, bool succeeded) =>
        Build(ActivityEventTypes.McpToolCall, solutionPath,
            new McpToolCallData(toolName, paramsSummary, resultSummary, durationMs, succeeded));

    public static ActivityEvent Reconnect(string? solutionPath) =>
        new(ActivityEventTypes.Reconnect, DateTimeOffset.UtcNow, solutionPath, null);

    private static ActivityEvent Build<T>(string type, string? solutionPath, T data)
    {
        var dataNode = JsonNode.Parse(JsonSerializer.Serialize(data, JsonRpcSerializer.Options));
        return new ActivityEvent(type, DateTimeOffset.UtcNow, solutionPath, dataNode);
    }
}
