// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Engine.Models;
using Piston.Hosting.Mapping;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Xunit;

namespace Piston.Tests.Mapping;

public sealed class ActivityEventMappingTests
{
    public static TheoryData<EngineActivityEvent, string> Events => new()
    {
        {
            new SolutionLoadedActivityEvent("/repo/Test.slnx", 3),
            """{"type":"solution.loaded","solutionPath":"/repo/Test.slnx","data":{"solutionPath":"/repo/Test.slnx","projectCount":3}}"""
        },
        {
            new FileChangesDetectedActivityEvent("/repo/Test.slnx", ["/repo/A.cs", "/repo/B.cs"]),
            """{"type":"file.changes","solutionPath":"/repo/Test.slnx","data":{"changedFiles":["/repo/A.cs","/repo/B.cs"],"count":2}}"""
        },
        {
            new FileChangesDetectedActivityEvent(null, []),
            """{"type":"file.changes","data":{"changedFiles":[],"count":0}}"""
        },
        {
            new BuildCompletedActivityEvent("/repo/Test.slnx", false, 12.5, ["CS0001", "CS0002"]),
            """{"type":"build.completed","solutionPath":"/repo/Test.slnx","data":{"succeeded":false,"durationMs":12.5,"errorCount":2,"errors":["CS0001","CS0002"]}}"""
        },
        {
            new BuildCompletedActivityEvent(null, true, 0, []),
            """{"type":"build.completed","data":{"succeeded":true,"durationMs":0,"errorCount":0,"errors":[]}}"""
        },
        {
            new TestRunCompletedActivityEvent("/repo/Test.slnx", 2, 2, 1, 42.5,
                [new TestFailure("Tests.Fails", "expected true"), new TestFailure("Tests.NoMessage", null)]),
            """{"type":"tests.completed","solutionPath":"/repo/Test.slnx","data":{"passed":2,"failed":2,"skipped":1,"durationMs":42.5,"failures":[{"fullyQualifiedName":"Tests.Fails","message":"expected true"},{"fullyQualifiedName":"Tests.NoMessage"}]}}"""
        },
        {
            new TestRunCompletedActivityEvent(null, 1, 0, 0, 0, []),
            """{"type":"tests.completed","data":{"passed":1,"failed":0,"skipped":0,"durationMs":0,"failures":[]}}"""
        },
    };

    [Theory]
    [MemberData(nameof(Events))]
    public void EngineActivity_PreservesWireShapeAndTimestamp(EngineActivityEvent activity, string expectedJson)
    {
        var timestamp = DateTimeOffset.Parse("2026-01-02T03:04:05+02:00");
        activity = activity with { Timestamp = timestamp };

        var dto = activity.ToDto();
        var actual = JsonSerializer.SerializeToNode(dto, JsonRpcSerializer.Options)!;
        var expected = JsonNode.Parse(expectedJson)!;
        expected["timestamp"] = JsonSerializer.SerializeToNode(timestamp);

        Assert.True(JsonNode.DeepEquals(expected, actual), actual.ToJsonString());
        var roundtrip = actual.Deserialize<ActivityEvent>(JsonRpcSerializer.Options)!;
        Assert.Equal(timestamp, roundtrip.Timestamp);
        Assert.Equal(activity.SolutionPath, roundtrip.SolutionPath);
        Assert.Equal(dto.Type, roundtrip.Type);
        Assert.True(JsonNode.DeepEquals(dto.Data, roundtrip.Data));
    }
}
