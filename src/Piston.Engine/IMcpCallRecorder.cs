namespace Piston.Engine;

/// <summary>
/// Abstraction for recording MCP tool call events.
/// Decouples Piston.Mcp tools from protocol-layer types.
/// </summary>
public interface IMcpCallRecorder
{
    void Record(string toolName, string? paramsSummary, string? resultSummary, double durationMs, bool succeeded);
}

/// <summary>No-op implementation for when call tracking is not configured.</summary>
public sealed class NullMcpCallRecorder : IMcpCallRecorder
{
    public static readonly NullMcpCallRecorder Instance = new();

    public void Record(string toolName, string? paramsSummary, string? resultSummary, double durationMs, bool succeeded)
    {
    }
}
