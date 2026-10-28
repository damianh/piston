// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Nodes;
using Piston.Engine.Models;
using Piston.Hosting.Protocol;
using Piston.Protocol.JsonRpc;
using Piston.Tests.Mcp;
using Xunit;

namespace Piston.Tests.Protocol;

public sealed class RunTestsCommandTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunFailed_WithNoFailures_ReportsTheReasonWithoutRunning()
    {
        var engine = new StubEngine();
        var ex = await Assert.ThrowsAsync<JsonRpcException>(() => new EngineCommandDispatcher(engine)
            .HandleCommandAsync(ProtocolMethods.EngineRunFailed, null, _ct));
        Assert.Equal("No failed tests to run.", ex.Message);
        Assert.Null(engine.LastSelection);
        Assert.False(engine.ForceRunCalled);
    }

    [Fact]
    public async Task RunFailed_UsesTheSameSelectionAsTheDashboard()
    {
        var engine = new StubEngine();
        engine.State.TestSuites =
        [
            new TestSuite("A.Tests",
                [new TestResult("A.C.Fail", "Fail", TestStatus.Failed, TimeSpan.Zero, null, null, null, null)],
                DateTimeOffset.UtcNow, TimeSpan.Zero),
        ];
        _ = await new EngineCommandDispatcher(engine).HandleCommandAsync(ProtocolMethods.EngineRunFailed, null, _ct);
        Assert.Equal(["A.Tests"], engine.LastSelection!.Projects);
        Assert.Equal(["A.C.Fail"], engine.LastSelection.Tests!);
    }

    [Fact]
    public async Task StartsTheRun_AndRepliesWithoutAwaitingIt()
    {
        var engine = new StubEngine();
        var dispatcher = new EngineCommandDispatcher(engine);

        var result = await dispatcher.HandleCommandAsync(
            ProtocolMethods.EngineRunTests,
            JsonNode.Parse("""{"projects":["A.Tests"],"tests":["A.C.M"]}"""),
            _ct);

        Assert.Null(result);
        Assert.Equal(["A.Tests"], engine.LastSelection!.Projects);
        Assert.Equal(["A.C.M"], engine.LastSelection.Tests!);
    }

    [Fact]
    public async Task WithoutTests_RunsWholeProjects()
    {
        var engine = new StubEngine();

        _ = await new EngineCommandDispatcher(engine).HandleCommandAsync(
            ProtocolMethods.EngineRunTests, JsonNode.Parse("""{"projects":["A.Tests"]}"""), _ct);

        Assert.Null(engine.LastSelection!.Tests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"projects":[]}""")]
    [InlineData("""{"projects":[" "]}""")]
    public async Task WithoutProjects_IsInvalidParams(string json)
    {
        var dispatcher = new EngineCommandDispatcher(new StubEngine());

        var ex = await Assert.ThrowsAsync<JsonRpcException>(() =>
            dispatcher.HandleCommandAsync(ProtocolMethods.EngineRunTests, JsonNode.Parse(json), _ct));

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public async Task UnknownProject_IsInvalidParams()
    {
        var engine = new StubEngine { RunSelectedError = new ArgumentException("'X' is not a test project.") };

        var ex = await Assert.ThrowsAsync<JsonRpcException>(() => new EngineCommandDispatcher(engine)
            .HandleCommandAsync(ProtocolMethods.EngineRunTests, JsonNode.Parse("""{"projects":["X"]}"""), _ct));

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, ex.Code);
        Assert.Equal("'X' is not a test project.", ex.Message);
    }

    [Fact]
    public async Task RunInProgress_IsReportedToTheCaller()
    {
        var engine = new StubEngine
        {
            RunSelectedRun = Task.FromException<TestRunRecord>(new InvalidOperationException("A test run is already in progress.")),
        };

        var ex = await Assert.ThrowsAsync<JsonRpcException>(() => new EngineCommandDispatcher(engine)
            .HandleCommandAsync(ProtocolMethods.EngineRunTests, JsonNode.Parse("""{"projects":["A.Tests"]}"""), _ct));

        Assert.Equal(JsonRpcErrorCodes.InvalidRequest, ex.Code);
        Assert.Equal("A test run is already in progress.", ex.Message);
    }
}
