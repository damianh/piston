using System.Text.Json.Nodes;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Xunit;

namespace Piston.Protocol.Tests.JsonRpc;

public sealed class JsonRpcSerializerTests
{
    // ── JsonRpcRequest round-trip ─────────────────────────────────────────────

    [Fact]
    public void SerializeRequest_RoundTrip_FieldsMatch()
    {
        var request = new JsonRpcRequest("req-1", "engine/forceRun");
        var bytes   = JsonRpcSerializer.Serialize(request);

        var result = JsonRpcSerializer.DeserializeRequest(bytes);

        Assert.Equal("2.0",               result.JsonRpc);
        Assert.Equal("req-1",             result.Id);
        Assert.Equal("engine/forceRun",   result.Method);
        Assert.Null(result.Params);
    }

    [Fact]
    public void SerializeRequest_WithParams_RoundTrip()
    {
        var paramsNode = JsonNode.Parse("{\"solutionPath\":\"/repo/foo.slnx\"}");
        var request    = new JsonRpcRequest("req-2", "engine/start", paramsNode);
        var bytes      = JsonRpcSerializer.Serialize(request);

        var result = JsonRpcSerializer.DeserializeRequest(bytes);

        Assert.Equal("req-2",           result.Id);
        Assert.Equal("engine/start",    result.Method);
        Assert.NotNull(result.Params);
        Assert.Equal("/repo/foo.slnx",  result.Params!["solutionPath"]!.GetValue<string>());
    }

    // ── JsonRpcNotification round-trip ────────────────────────────────────────

    [Fact]
    public void SerializeNotification_RoundTrip_FieldsMatch()
    {
        var notification = new JsonRpcNotification("engine/phaseChanged",
            JsonNode.Parse("{\"phase\":\"Testing\",\"detail\":null}"));
        var bytes = JsonRpcSerializer.Serialize(notification);

        var result = JsonRpcSerializer.DeserializeNotification(bytes);

        Assert.Equal("2.0",                result.JsonRpc);
        Assert.Equal("engine/phaseChanged", result.Method);
        Assert.NotNull(result.Params);
    }

    // ── JsonRpcResponse round-trip ────────────────────────────────────────────

    [Fact]
    public void SerializeResponse_Success_RoundTrip()
    {
        var response = new JsonRpcResponse("req-3", JsonNode.Parse("null"));
        var bytes    = JsonRpcSerializer.Serialize(response);

        var result = JsonRpcSerializer.DeserializeResponse(bytes);

        Assert.Equal("2.0",   result.JsonRpc);
        Assert.Equal("req-3", result.Id);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SerializeResponse_Error_RoundTrip()
    {
        var response = new JsonRpcResponse("req-4", Error: new JsonRpcError(-32601, "Method not found"));
        var bytes    = JsonRpcSerializer.Serialize(response);

        var result = JsonRpcSerializer.DeserializeResponse(bytes);

        Assert.Equal("req-4",            result.Id);
        Assert.NotNull(result.Error);
        Assert.Equal(-32601,             result.Error!.Code);
        Assert.Equal("Method not found", result.Error.Message);
        Assert.Null(result.Error.Data);
    }

    // ── StartCommand params ───────────────────────────────────────────────────

    [Fact]
    public void SerializeStartCommandParams_RoundTrip_SolutionPathMatches()
    {
        var cmd        = new StartCommand("/path/to/solution.slnx");
        var paramsNode = JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(cmd, JsonRpcSerializer.Options));

        var result = JsonRpcSerializer.DeserializeParams<StartCommand>(paramsNode);

        Assert.NotNull(result);
        Assert.Equal("/path/to/solution.slnx", result!.SolutionPath);
    }

    // ── StateSnapshotNotification deep equality ───────────────────────────────

    [Fact]
    public void SerializeStateSnapshot_RoundTrip_DeepEquality()
    {
        var snapshot = new StateSnapshotNotification(
            Phase:                    PistonPhaseDto.Testing,
            Suites:                   [new TestSuiteDto("MySuite",
                                          [new TestResultDto("FQN.Test1", "Test1", TestStatusDto.Passed, 42.0, null, null, null, null)],
                                          DateTimeOffset.UtcNow,
                                          42.0)],
            InProgressSuites:         [],
            LastBuild:                null,
            LastRunTime:              DateTimeOffset.UtcNow,
            LastBuildDurationMs:      100.0,
            LastTestDurationMs:       200.0,
            LastTestRunnerError:      null,
            TotalExpectedTests:       1,
            CompletedTests:           1,
            VerifiedSinceChangeCount: 1,
            LastFileChangeTime:       null,
            SolutionPath:             "/repo/foo.slnx",
            AffectedProjects:         null,
            AffectedTestProjects:     null,
            LastChangedFiles:         null,
            CoverageEnabled:          false,
            HasCoverageData:          false,
            CoverageImpactDetail:     null,
            TotalTestProjects:        1,
            CompletedTestProjects:    1,
            ProjectStatuses:          null
        );

        var paramsNode = JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(snapshot, JsonRpcSerializer.Options));

        var result = JsonRpcSerializer.DeserializeParams<StateSnapshotNotification>(paramsNode);

        Assert.NotNull(result);
        Assert.Equal(PistonPhaseDto.Testing, result!.Phase);
        Assert.Single(result.Suites);
        Assert.Equal("MySuite",              result.Suites[0].Name);
        Assert.Single(result.Suites[0].Tests);
        Assert.Equal("Test1",                result.Suites[0].Tests[0].DisplayName);
        Assert.Equal(TestStatusDto.Passed,   result.Suites[0].Tests[0].Status);
        Assert.Equal("/repo/foo.slnx",       result.SolutionPath);
    }

    // ── Enum serialization ────────────────────────────────────────────────────

    [Fact]
    public void EnumSerialization_PistonPhase_SerializesAsString()
    {
        var notification = new JsonRpcNotification("engine/phaseChanged",
            JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
                new PhaseChangedNotification(PistonPhaseDto.Testing, null),
                JsonRpcSerializer.Options)));

        var bytes  = JsonRpcSerializer.Serialize(notification);
        var json   = System.Text.Encoding.UTF8.GetString(bytes.Span);

        Assert.Contains("\"testing\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnumDeserialization_PistonPhase_RoundTrips()
    {
        var original = new PhaseChangedNotification(PistonPhaseDto.Testing, null);
        var node     = JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(original, JsonRpcSerializer.Options));
        var result   = JsonRpcSerializer.DeserializeParams<PhaseChangedNotification>(node);

        Assert.NotNull(result);
        Assert.Equal(PistonPhaseDto.Testing, result!.Phase);
    }

    // ── Null params ───────────────────────────────────────────────────────────

    [Fact]
    public void DeserializeParams_NullNode_ReturnsDefault()
    {
        var result = JsonRpcSerializer.DeserializeParams<StartCommand>(null);
        Assert.Null(result);
    }

    // ── Unknown fields ignored ────────────────────────────────────────────────

    [Fact]
    public void DeserializeMessage_UnknownFields_Ignored()
    {
        var json  = "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"engine/forceRun\",\"unknownField\":\"value\"}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);

        var result = JsonRpcSerializer.DeserializeRequest(bytes);

        Assert.Equal("1",                result.Id);
        Assert.Equal("engine/forceRun",  result.Method);
    }

    // ── DeserializeMessage discriminator ─────────────────────────────────────

    [Fact]
    public void DeserializeMessage_Request_ReturnsJsonRpcRequest()
    {
        var request = new JsonRpcRequest("x", "engine/stop");
        var bytes   = JsonRpcSerializer.Serialize(request);

        var msg = JsonRpcSerializer.DeserializeMessage(bytes);

        Assert.IsType<JsonRpcRequest>(msg);
    }

    [Fact]
    public void DeserializeMessage_Notification_ReturnsJsonRpcNotification()
    {
        var notification = new JsonRpcNotification("engine/phaseChanged");
        var bytes        = JsonRpcSerializer.Serialize(notification);

        var msg = JsonRpcSerializer.DeserializeMessage(bytes);

        Assert.IsType<JsonRpcNotification>(msg);
    }

    [Fact]
    public void DeserializeMessage_Response_ReturnsJsonRpcResponse()
    {
        var response = new JsonRpcResponse("x");
        var bytes    = JsonRpcSerializer.Serialize(response);

        var msg = JsonRpcSerializer.DeserializeMessage(bytes);

        Assert.IsType<JsonRpcResponse>(msg);
    }
}
