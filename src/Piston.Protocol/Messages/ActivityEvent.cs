using System.Text.Json.Nodes;

namespace Piston.Protocol.Messages;

/// <summary>Wire-level discriminated union for activity timeline events.</summary>
public sealed record ActivityEvent(
    string Type,
    DateTimeOffset Timestamp,
    string? SolutionPath,
    JsonNode? Data
);

/// <summary>Discriminator constants for <see cref="ActivityEvent.Type"/>.</summary>
public static class ActivityEventTypes
{
    public const string SolutionLoaded        = "solution.loaded";
    public const string FileChangesDetected   = "file.changes";
    public const string BuildCompleted        = "build.completed";
    public const string TestRunCompleted      = "tests.completed";
    public const string McpToolCall           = "mcp.toolCall";
    public const string Reconnect             = "reconnect";
}

// ── Concrete event data records (serialized into ActivityEvent.Data) ──────

public sealed record SolutionLoadedData(
    string SolutionPath,
    int ProjectCount
);

public sealed record FileChangesDetectedData(
    IReadOnlyList<string> ChangedFiles,
    int Count
);

public sealed record BuildCompletedData(
    bool Succeeded,
    double DurationMs,
    int ErrorCount,
    IReadOnlyList<string> Errors
);

public sealed record TestFailureData(
    string FullyQualifiedName,
    string? Message
);

public sealed record TestRunCompletedData(
    int Passed,
    int Failed,
    int Skipped,
    double DurationMs,
    IReadOnlyList<TestFailureData> Failures
);

public sealed record McpToolCallData(
    string ToolName,
    string? ParamsSummary,
    string? ResultSummary,
    double DurationMs,
    bool Succeeded
);
