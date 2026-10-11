using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Engine.Models;

namespace Piston.Mcp.Tools;

/// <summary>Serializes a <see cref="TestRunRecord"/> as the <c>piston.test-run/v1</c> JSON document.</summary>
public static class TestRunJson
{
    public const string Schema = "piston.test-run/v1";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string Serialize(TestRunRecord run) => ToJson(run).ToJsonString(WriteOptions);

    public static JsonObject ToJson(TestRunRecord run)
    {
        var tests = run.Tests.ToList();
        var testArray = new JsonArray();
        foreach (var t in tests.OrderBy(t => t.FullyQualifiedName, StringComparer.Ordinal))
        {
            testArray.Add(new JsonObject
            {
                ["fullyQualifiedName"] = t.FullyQualifiedName,
                ["displayName"] = t.DisplayName,
                ["status"] = t.Status.ToString(),
                ["durationMs"] = Math.Round(t.Duration.TotalMilliseconds, 3),
                ["errorMessage"] = t.ErrorMessage,
            });
        }

        return new JsonObject
        {
            ["schema"] = Schema,
            ["runId"] = run.RunId,
            ["engineInstanceId"] = run.EngineInstanceId,
            ["sequence"] = run.Sequence,
            ["trigger"] = run.Trigger.ToString(),
            ["status"] = run.Status.ToString(),
            ["statusReason"] = run.StatusReason,
            ["isTerminal"] = run.IsTerminal,
            ["scope"] = run.Scope?.ToString(),
            ["isFiltered"] = run.IsFiltered,
            ["isFullScope"] = run.IsFullScope,
            ["freshness"] = run.Freshness.ToString(),
            ["freshnessReason"] = run.FreshnessReason,
            ["isCurrentFullResult"] = run.IsCurrentFullResult,
            ["requestedAt"] = run.RequestedAt.ToString("O"),
            ["startedAt"] = run.StartedAt?.ToString("O"),
            ["completedAt"] = run.CompletedAt?.ToString("O"),
            ["sourceBefore"] = Fingerprint(run.SourceBefore),
            ["sourceAfter"] = Fingerprint(run.SourceAfter),
            ["sourceChangeObserved"] = run.SourceChangeObserved,
            ["context"] = Context(run.Context),
            ["projects"] = new JsonArray(run.Projects
                .Select(p => (JsonNode)new JsonObject
                {
                    ["projectPath"] = p.ProjectPath,
                    ["status"] = p.Status.ToString(),
                    ["testCount"] = p.TestCount,
                })
                .ToArray()),
            ["runnerError"] = run.RunnerError,
            ["summary"] = new JsonObject
            {
                ["total"] = tests.Count,
                ["passed"] = tests.Count(t => t.Status == TestStatus.Passed),
                ["failed"] = tests.Count(t => t.Status == TestStatus.Failed),
                ["skipped"] = tests.Count(t => t.Status == TestStatus.Skipped),
                ["other"] = tests.Count(t => t.Status is not (TestStatus.Passed or TestStatus.Failed or TestStatus.Skipped)),
            },
            ["duplicateTestIds"] = new JsonArray(run.DuplicateTestIds.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
            ["tests"] = testArray,
        };
    }

    private static JsonObject? Fingerprint(SourceFingerprint? fp) => fp is null
        ? null
        : new JsonObject
        {
            ["algorithm"] = fp.Algorithm,
            ["value"] = fp.Value,
            ["fileCount"] = fp.FileCount,
        };

    private static JsonObject? Context(TestRunContext? c) => c is null
        ? null
        : new JsonObject
        {
            ["solutionPath"] = c.SolutionPath,
            ["testProjects"] = Strings(c.TestProjects),
            ["buildTargets"] = Strings(c.BuildTargets),
            ["userFilter"] = c.UserFilter,
            ["effectiveFilter"] = c.EffectiveFilter,
            ["coverageSelectedTestCount"] = c.CoverageSelectedTestCount,
            ["coverageEnabled"] = c.CoverageEnabled,
            ["buildConfiguration"] = c.BuildConfiguration,
            ["sdkVersion"] = c.SdkVersion,
            ["runtime"] = c.Runtime,
            ["operatingSystem"] = c.OperatingSystem,
            ["workingDirectory"] = c.WorkingDirectory,
            ["testWorkingDirectories"] = c.TestWorkingDirectories is null ? null
                : new JsonObject(c.TestWorkingDirectories.Select(pair =>
                    new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value)))),
        };

    private static JsonArray? Strings(IReadOnlyList<string>? values) => values is null
        ? null
        : new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
}
