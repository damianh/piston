// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Nodes;
using Piston.Engine;
using Piston.Engine.Models;
using Piston.Hosting.Mapping;
using Piston.Hosting.Protocol;
using Piston.Protocol.JsonRpc;
using Piston.Tests.Mcp;
using Xunit;

namespace Piston.Tests.Mapping;

public sealed class RunLogTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void FindRunLog_ReturnsTheLogOfTheProjectsLatestRun()
    {
        var other = Log("B.Tests.csproj", "other project");
        var latest = Log("A.Tests.csproj", "multi-targeted run");
        var state = new PistonState
        {
            TestSuites =
            [
                Suite("B.Tests", other, "run-1", Test("B.T1", null)),
                Suite("A.Tests", latest, "run-2", Test("A.T1", "net8.0"), Test("A.T1", "net10.0")),
            ],
        };

        var dto = state.FindRunLog("A.Tests");

        Assert.NotNull(dto);
        Assert.Equal("run-2", dto.RunId);
        Assert.Equal("VSTest", dto.Runner);
        Assert.Equal(1, dto.ExitCode);
        Assert.Equal("multi-targeted run", Assert.Single(dto.Entries).Message);
    }

    [Fact]
    public void FindRunLog_DuplicateNamesAreSelectedByProjectId()
    {
        var state = new PistonState
        {
            TestSuites =
            [
                Suite("Tests", Log("One.Tests.csproj", "first"), "run-1", Test("T.One", null)) with { ProjectId = "One/Tests.csproj" },
                Suite("Tests", Log("Two.Tests.csproj", "second"), "run-2", Test("T.Two", null)) with { ProjectId = "Two/Tests.csproj" },
            ],
        };

        Assert.Equal("first", Assert.Single(state.FindRunLog("One/Tests.csproj")!.Entries).Message);
        Assert.Equal("second", Assert.Single(state.FindRunLog("Two/Tests.csproj")!.Entries).Message);
        Assert.Null(state.FindRunLog("Tests"));
    }

    [Fact]
    public void FindRunLog_AfterASelectiveRun_ReturnsTheLatestSuitesLog()
    {
        // A selective run keeps the project's untouched results in the earlier suite and appends its own.
        var state = new PistonState
        {
            TestSuites =
            [
                Suite("A.Tests", Log("A.Tests.csproj", "full run"), "run-1", Test("A.T1", null)),
                Suite("A.Tests", Log("A.Tests.csproj", "selective run"), "run-2", Test("A.T2", null))
                    with { Timestamp = Now.AddMinutes(1) },
            ],
        };

        var dto = state.FindRunLog("A.Tests");

        Assert.NotNull(dto);
        Assert.Equal("run-2", dto.RunId);
        Assert.Equal("selective run", Assert.Single(dto.Entries).Message);
    }

    [Fact]
    public void FindRunLog_UnknownProjectOrNoLog_ReturnsNull()
    {
        var state = new PistonState
        {
            TestSuites = [Suite("A.Tests", log: null, runId: null, Test("A.T1", null))],
        };

        Assert.Null(state.FindRunLog("A.Tests"));
        Assert.Null(state.FindRunLog("a.tests"));
        Assert.Null(state.FindRunLog("Missing.Tests"));
    }

    [Fact]
    public async Task Dispatcher_GetRunLog_ReturnsTheLog()
    {
        var engine = new StubEngine();
        engine.State.TestSuites = [Suite("A.Tests", Log("A.Tests.csproj", "hello"), "run-1", Test("A.T1", null))];
        var dispatcher = new EngineCommandDispatcher(engine);

        var result = await dispatcher.HandleCommandAsync(
            ProtocolMethods.TestsGetRunLog, JsonNode.Parse("""{"projectId":"A.Tests"}"""), _ct);

        var log = result!["log"]!;
        Assert.Equal("A.Tests.csproj", log["projectPath"]!.GetValue<string>());
        Assert.Equal("hello", log["entries"]![0]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Dispatcher_GetRunLog_WithoutProject_IsInvalidParams()
    {
        var dispatcher = new EngineCommandDispatcher(new StubEngine());

        var ex = await Assert.ThrowsAsync<JsonRpcException>(() =>
            dispatcher.HandleCommandAsync(ProtocolMethods.TestsGetRunLog, JsonNode.Parse("{}"), _ct));

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, ex.Code);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(2100)] // beyond what the log retains, so it already has an omitted marker
    public async Task Dispatcher_GetRunLog_FullLogOfLongLines_FitsInOneMessage(int lines)
    {
        // Every character of this filler needs escaping in JSON, the worst case for the serialized size.
        var filler = new string('\u0001', 4000);
        var log = new ExecutionLog("A.Tests.csproj") { Runner = "VSTest", ExitCode = 1 };
        for (var i = 0; i < lines; i++)
        {
            log.Append("stdout", $"line {i}:\"<é>\n{filler}");
        }

        var engine = new StubEngine();
        engine.State.TestSuites = [Suite("A.Tests", log, "run-1", Test("A.T1", null))];
        var dispatcher = new EngineCommandDispatcher(engine);

        var result = await dispatcher.HandleCommandAsync(
            ProtocolMethods.TestsGetRunLog, JsonNode.Parse("""{"projectId":"A.Tests"}"""), _ct);
        var message = JsonRpcSerializer.Serialize(new JsonRpcResponse("1", result));

        Assert.True(
            message.Length < MessageFramer.DefaultMaxMessageSize,
            $"Serialized run log is {message.Length} bytes.");

        var entries = result!["log"]!["entries"]!.AsArray();
        var marker = Assert.Single(entries, e => e!["category"]!.GetValue<string>() == ExecutionLog.OmittedCategory);
        var omitted = result["log"]!["omittedEntries"]!.GetValue<int>();
        Assert.Contains($"{omitted} line(s) omitted", marker!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(lines, entries.Count - 1 + omitted);
        Assert.StartsWith("line 0:", entries[0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith($"line {lines - 1}:", entries[^1]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private static ExecutionLog Log(string project, string message)
    {
        var log = new ExecutionLog(project) { Runner = "VSTest", ExitCode = 1 };
        log.Append("stdout", message);
        log.Complete();
        return log;
    }

    private static TestResult Test(string fqn, string? tfm) =>
        new(fqn, fqn, TestStatus.Passed, TimeSpan.Zero, null, null, null, null) { TargetFramework = tfm };

    private static TestSuite Suite(string name, ExecutionLog? log, string? runId, params TestResult[] tests) =>
        new(name, tests, Now, TimeSpan.Zero) { ExecutionLog = log, RunId = runId, ProjectId = name };
}
