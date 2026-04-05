using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Xunit;

namespace Piston.Protocol.Tests.JsonRpc;

/// <summary>
/// Roundtrip tests that verify the STJ source-gen context produces identical output
/// to reflection-based serialization and that all wire-format invariants are preserved.
/// </summary>
public sealed class PistonJsonContextTests
{
    // Reference options without TypeInfoResolver for comparison
    private static readonly JsonSerializerOptions ReflectionOptions = new()
    {
        PropertyNamingPolicy       = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling             = JsonNumberHandling.AllowReadingFromString,
        Converters                 = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented              = false,
    };

    // ── StateSnapshotNotification (most complex type) ─────────────────────────

    [Fact]
    public void StateSnapshotNotification_Roundtrip_FieldsMatch()
    {
        var snapshot = BuildSnapshot();
        var bytes    = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonRpcSerializer.Options);
        var result   = JsonSerializer.Deserialize<StateSnapshotNotification>(bytes, JsonRpcSerializer.Options);

        Assert.NotNull(result);
        Assert.Equal(PistonPhaseDto.Testing, result.Phase);
        Assert.Single(result.Suites);
        Assert.Equal("MyTest.Suite", result.Suites[0].Name);
        Assert.Single(result.Suites[0].Tests);
        Assert.Equal("Passed", result.Suites[0].Tests[0].Status.ToString().ToLower() == "passed" ? "Passed" : result.Suites[0].Tests[0].Status.ToString());
        Assert.Equal(1, result.TotalExpectedTests);
        Assert.Equal(1, result.CompletedTests);
        Assert.NotNull(result.ProjectStatuses);
        Assert.Equal(ProjectRunStatusDto.Completed, result.ProjectStatuses!["proj1"]);
    }

    [Fact]
    public void StateSnapshotNotification_WireFormat_CamelCaseKeys()
    {
        var snapshot = BuildSnapshot();
        var json     = JsonSerializer.Serialize(snapshot, JsonRpcSerializer.Options);

        Assert.Contains("\"phase\":", json);
        Assert.Contains("\"suites\":", json);
        Assert.Contains("\"totalExpectedTests\":", json);
        Assert.Contains("\"projectStatuses\":", json);
        Assert.DoesNotContain("\"Phase\":", json);
    }

    [Fact]
    public void StateSnapshotNotification_WireFormat_EnumAsString()
    {
        var snapshot = BuildSnapshot();
        var json     = JsonSerializer.Serialize(snapshot, JsonRpcSerializer.Options);

        Assert.Contains("\"testing\"", json);     // PistonPhaseDto.Testing as camelCase string
        Assert.Contains("\"passed\"", json);       // TestStatusDto.Passed as camelCase string
        Assert.Contains("\"completed\"", json);    // ProjectRunStatusDto.Completed as camelCase string
    }

    [Fact]
    public void StateSnapshotNotification_WireFormat_NullOmission()
    {
        var snapshot = BuildSnapshot() with { SolutionPath = null, LastBuild = null };
        var json     = JsonSerializer.Serialize(snapshot, JsonRpcSerializer.Options);

        Assert.DoesNotContain("\"solutionPath\"", json);
        Assert.DoesNotContain("\"lastBuild\"", json);
    }

    // ── IReadOnlyList / IReadOnlyDictionary concrete collection roundtrip ──────

    [Fact]
    public void TestSuiteDto_WithReadOnlyListTests_Roundtrip()
    {
        var suite = new TestSuiteDto(
            "Suite1",
            new List<TestResultDto>
            {
                new("ns.Test1", "Test1", TestStatusDto.Passed, 10.0, null, null, null, null),
                new("ns.Test2", "Test2", TestStatusDto.Failed, 5.0,  "output", "assertion failed", "at ...", null),
            },
            DateTimeOffset.UtcNow,
            15.0);

        var bytes  = JsonSerializer.SerializeToUtf8Bytes(suite, JsonRpcSerializer.Options);
        var result = JsonSerializer.Deserialize<TestSuiteDto>(bytes, JsonRpcSerializer.Options);

        Assert.NotNull(result);
        Assert.Equal(2, result.Tests.Count);
        Assert.Equal(TestStatusDto.Passed, result.Tests[0].Status);
        Assert.Equal(TestStatusDto.Failed, result.Tests[1].Status);
        Assert.Equal("assertion failed", result.Tests[1].ErrorMessage);
        Assert.Null(result.Tests[0].ErrorMessage);  // null omission roundtrip
    }

    [Fact]
    public void FileCoverageUpdatedNotification_WithCoverageLines_Roundtrip()
    {
        var notification = new FileCoverageUpdatedNotification(
            "/src/Foo.cs",
            new List<CoverageLineDto>
            {
                new(1, 3,  "covered"),
                new(2, 0,  "uncovered"),
                new(3, -1, "notCoverable"),
            });

        var bytes  = JsonSerializer.SerializeToUtf8Bytes(notification, JsonRpcSerializer.Options);
        var result = JsonSerializer.Deserialize<FileCoverageUpdatedNotification>(bytes, JsonRpcSerializer.Options);

        Assert.NotNull(result);
        Assert.Equal("/src/Foo.cs", result.FilePath);
        Assert.Equal(3, result.Lines.Count);
        Assert.Equal(3,    result.Lines[0].HitCount);
        Assert.Equal(0,    result.Lines[1].HitCount);
        Assert.Equal(-1,   result.Lines[2].HitCount);
        Assert.Equal("notCoverable", result.Lines[2].Status);
    }

    [Fact]
    public void BuildResultDto_WithStringLists_Roundtrip()
    {
        var build = new BuildResultDto(
            BuildStatusDto.Failed,
            new List<string> { "Error CS0001", "Error CS0002" },
            new List<string> { "Warning CS0612" },
            123.4);

        var bytes  = JsonSerializer.SerializeToUtf8Bytes(build, JsonRpcSerializer.Options);
        var result = JsonSerializer.Deserialize<BuildResultDto>(bytes, JsonRpcSerializer.Options);

        Assert.NotNull(result);
        Assert.Equal(BuildStatusDto.Failed, result.Status);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal("Error CS0001", result.Errors[0]);
        Assert.Single(result.Warnings);
        Assert.Equal(BuildStatusDto.Failed, result.Status);
    }

    // ── Envelope types with JsonNode? ─────────────────────────────────────────

    [Fact]
    public void RequestEnvelope_WithComplexParamsNode_Roundtrip()
    {
        var paramsNode = JsonNode.Parse("{\"solutionPath\":\"/repo/foo.slnx\",\"extra\":{\"nested\":42}}");
        var request    = new JsonRpcRequest("id-1", "engine/start", paramsNode);
        var bytes      = JsonRpcSerializer.Serialize(request);
        var result     = JsonRpcSerializer.DeserializeRequest(bytes);

        Assert.Equal("id-1",           result.Id);
        Assert.Equal("engine/start",   result.Method);
        Assert.NotNull(result.Params);
        Assert.Equal("/repo/foo.slnx", result.Params!["solutionPath"]!.GetValue<string>());
        Assert.Equal(42,               result.Params["extra"]!["nested"]!.GetValue<int>());
    }

    [Fact]
    public void ResponseEnvelope_WithResultNode_Roundtrip()
    {
        var resultNode = JsonNode.Parse("{\"filePath\":\"/src/Foo.cs\",\"lines\":[]}");
        var response   = new JsonRpcResponse("id-2", resultNode, null);
        var bytes      = JsonRpcSerializer.Serialize(response);
        var result     = JsonRpcSerializer.DeserializeResponse(bytes);

        Assert.Equal("id-2",          result.Id);
        Assert.NotNull(result.Result);
        Assert.Equal("/src/Foo.cs",   result.Result!["filePath"]!.GetValue<string>());
        Assert.Null(result.Error);
    }

    [Fact]
    public void NotificationEnvelope_WithNullParams_Roundtrip()
    {
        var notification = new JsonRpcNotification("engine/forceRun", null);
        var bytes        = JsonRpcSerializer.Serialize(notification);
        var result       = JsonRpcSerializer.DeserializeNotification(bytes);

        Assert.Equal("engine/forceRun", result.Method);
        Assert.Null(result.Params);
    }

    // ── Wire format parity: source-gen == reflection ──────────────────────────

    [Fact]
    public void WireFormatParity_StateSnapshotNotification_MatchesReflection()
    {
        var snapshot      = BuildSnapshot();
        var sourceGenJson = JsonSerializer.Serialize(snapshot, JsonRpcSerializer.Options);
        var reflectJson   = JsonSerializer.Serialize(snapshot, ReflectionOptions);

        Assert.Equal(reflectJson, sourceGenJson);
    }

    [Fact]
    public void WireFormatParity_TestSuiteDto_MatchesReflection()
    {
        var suite = new TestSuiteDto(
            "Suite1",
            new List<TestResultDto>
            {
                new("ns.Test1", "Test1", TestStatusDto.Passed, 10.0, null, null, null, "source.cs"),
            },
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            10.0);

        var sourceGenJson = JsonSerializer.Serialize(suite, JsonRpcSerializer.Options);
        var reflectJson   = JsonSerializer.Serialize(suite, ReflectionOptions);

        Assert.Equal(reflectJson, sourceGenJson);
    }

    [Fact]
    public void WireFormatParity_FileCoverageUpdatedNotification_MatchesReflection()
    {
        var notification = new FileCoverageUpdatedNotification(
            "/src/Bar.cs",
            new List<CoverageLineDto> { new(5, 2, "covered") });

        var sourceGenJson = JsonSerializer.Serialize(notification, JsonRpcSerializer.Options);
        var reflectJson   = JsonSerializer.Serialize(notification, ReflectionOptions);

        Assert.Equal(reflectJson, sourceGenJson);
    }

    // ── Named param records ───────────────────────────────────────────────────

    [Fact]
    public void StartCommandParams_Roundtrip_CamelCase()
    {
        var @params = new StartCommandParams("/solution/path.slnx");
        var json    = JsonSerializer.Serialize(@params, JsonRpcSerializer.Options);
        var result  = JsonSerializer.Deserialize<StartCommandParams>(json, JsonRpcSerializer.Options);

        Assert.Contains("\"solutionPath\"", json);
        Assert.NotNull(result);
        Assert.Equal("/solution/path.slnx", result.SolutionPath);
    }

    [Fact]
    public void SetFilterCommandParams_NullFilter_NullOmitted()
    {
        var @params = new SetFilterCommandParams(null);
        var json    = JsonSerializer.Serialize(@params, JsonRpcSerializer.Options);

        Assert.DoesNotContain("\"filter\"", json);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static StateSnapshotNotification BuildSnapshot() =>
        new(
            Phase:                    PistonPhaseDto.Testing,
            Suites:                   new List<TestSuiteDto>
            {
                new("MyTest.Suite",
                    new List<TestResultDto>
                    {
                        new("MyTest.Suite.Test1", "Test1", TestStatusDto.Passed, 12.0, null, null, null, null),
                    },
                    DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    12.0),
            },
            InProgressSuites:         new List<TestSuiteDto>(),
            LastBuild:                new BuildResultDto(
                                          BuildStatusDto.Succeeded,
                                          new List<string>(),
                                          new List<string>(),
                                          55.0),
            LastRunTime:              DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            LastBuildDurationMs:      55.0,
            LastTestDurationMs:       12.0,
            LastTestRunnerError:      null,
            TotalExpectedTests:       1,
            CompletedTests:           1,
            VerifiedSinceChangeCount: 3,
            LastFileChangeTime:       null,
            SolutionPath:             "/repo/solution.slnx",
            AffectedProjects:         new List<string> { "proj1" },
            AffectedTestProjects:     new List<string> { "proj1.Tests" },
            LastChangedFiles:         new List<string> { "src/Foo.cs" },
            CoverageEnabled:          false,
            HasCoverageData:          false,
            CoverageImpactDetail:     null,
            TotalTestProjects:        1,
            CompletedTestProjects:    1,
            ProjectStatuses:          new Dictionary<string, ProjectRunStatusDto>
            {
                ["proj1"] = ProjectRunStatusDto.Completed,
            }
        );
}
