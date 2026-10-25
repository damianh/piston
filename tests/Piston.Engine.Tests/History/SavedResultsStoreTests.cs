// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Nodes;
using Piston.Engine.Execution;
using Piston.Engine.History;
using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.History;

public sealed class SavedResultsStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("piston-saved-results-").FullName;
    private readonly string _solution;
    private readonly List<string> _messages = [];
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public SavedResultsStoreTests()
    {
        _solution = Path.Combine(_root, "App.slnx");
        File.WriteAllText(_solution, "<Solution />");
    }

    private string FilePath => Path.Combine(_root, ".piston", "App.results.json");

    [Fact]
    public void Saves_and_restores_the_latest_result_snapshot()
    {
        var expected = Snapshot();
        using (var store = Open())
        {
            store.Schedule(expected);
        }

        using var reader = Open();
        var restored = reader.TryLoad();

        Assert.NotNull(restored);
        var expectedSuite = Assert.Single(expected.TestSuites);
        var restoredSuite = Assert.Single(restored.TestSuites);
        Assert.Equal(expectedSuite.Name, restoredSuite.Name);
        Assert.Equal(expectedSuite.ProjectId, restoredSuite.ProjectId);
        Assert.Equivalent(Assert.Single(expectedSuite.Tests), Assert.Single(restoredSuite.Tests), strict: true);
        Assert.Equal(expected.LastRun?.RunId, restored.LastRun?.RunId);
        Assert.Equivalent(expected.LastRun!.FailureClassifications, restored.LastRun!.FailureClassifications, strict: true);
        Assert.Equivalent(expected.LastRun.Projects, restored.LastRun.Projects, strict: true);
        Assert.Equal(expected.LastBuild!.Status, restored.LastBuild!.Status);
        Assert.Equal(expected.LastBuild.Duration, restored.LastBuild.Duration);
        Assert.Equal(expected.LastBuild.Errors, restored.LastBuild.Errors);
        Assert.Equal(expected.LastTestDuration, restored.LastTestDuration);
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void Saves_last_run_results_only_under_suites()
    {
        using (var store = Open())
        {
            store.Schedule(Snapshot());
        }

        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        var run = json["LastRun"]!.AsObject();
        Assert.False(run.ContainsKey("Tests"));
        _ = Assert.Single(run["Suites"]![0]!["Tests"]!.AsArray());
        using var reader = Open();
        Assert.Equivalent(Result, Assert.Single(reader.TryLoad()!.LastRun!.Tests), strict: true);
    }

    [Theory]
    [InlineData("TestSuites", null)]
    [InlineData("TestSuites/0", null)]
    [InlineData("TestSuites/0/Name", null)]
    [InlineData("TestSuites/0/Name", "<missing>")]
    [InlineData("TestSuites/0/Tests", null)]
    [InlineData("TestSuites/0/Tests/0", null)]
    [InlineData("TestSuites/0/Tests/0/FullyQualifiedName", null)]
    [InlineData("TestSuites/0/Tests/0/DisplayName", null)]
    [InlineData("TestSuites/0/Tests/0/Attempts", null)]
    [InlineData("TestSuites/0/Tests/0/Attempts", "[null]")]
    [InlineData("LastBuild/Errors", null)]
    [InlineData("LastBuild/Warnings", "[null]")]
    [InlineData("LastRun/RunId", null)]
    [InlineData("LastRun/RunId", "<missing>")]
    [InlineData("LastRun/EngineInstanceId", null)]
    [InlineData("LastRun/Suites", null)]
    [InlineData("LastRun/Suites/0", null)]
    [InlineData("LastRun/Suites/0/Tests/0", null)]
    [InlineData("LastRun/Suites/0/Tests/0/Attempts", "[null]")]
    [InlineData("LastRun/Projects", null)]
    [InlineData("LastRun/Projects", "[null]")]
    [InlineData("LastRun/Projects", "[{\"ProjectPath\":null,\"Status\":\"Completed\",\"TestCount\":1}]")]
    [InlineData("LastRun/Projects/0/Resources/Needs", null)]
    [InlineData("LastRun/Projects/0/Resources/Needs/Keys", null)]
    [InlineData("LastRun/Projects/0/Resources/Needs/Keys", "[null]")]
    [InlineData("LastRun/Projects/0/Resources/Needs/Reasons", null)]
    [InlineData("LastRun/Projects/0/Resources/Needs/Reasons", "[null]")]
    [InlineData("LastRun/Projects/0/Resources/Warnings", null)]
    [InlineData("LastRun/Projects/0/Resources/Warnings", "[null]")]
    [InlineData("LastRun/Projects/0/Resources/Wait/Keys", null)]
    [InlineData("LastRun/Projects/0/Resources/Wait/Keys", "[null]")]
    [InlineData("LastRun/Projects/0/Resources/Wait/BlockedBy", null)]
    [InlineData("LastRun/Projects/0/Resources/Wait/BlockedBy", "[null]")]
    [InlineData("LastRun/Build/Errors", "[null]")]
    [InlineData("LastRun/Context/Runtime", null)]
    [InlineData("LastRun/Context/SolutionPath", "\"\"")]
    [InlineData("LastRun/Context/SolutionPath", "\"   \"")]
    [InlineData("LastRun/Context/SolutionPath", "\"\\u0000\"")]
    [InlineData("LastRun/Context/TestProjects", "[\"\"]")]
    [InlineData("LastRun/Context/TestProjects", "[\"   \"]")]
    [InlineData("LastRun/Context/TestProjects", "[\"\\u0000\"]")]
    [InlineData("LastRun/Context/TestProjects", "[null]")]
    [InlineData("LastRun/Context/BuildTargets", "[null]")]
    [InlineData("LastRun/Context/TestWorkingDirectories", "{\"App.Tests\":null}")]
    [InlineData("LastRun/Context/SelectionReasons", "{\"App.Tests\":null}")]
    [InlineData("LastRun/Context/SelectionReasons", "{\"App.Tests\":[null]}")]
    [InlineData("LastRun/DuplicateTestIds", "[null]")]
    [InlineData("LastRun/PossiblyHangingTests", "[null]")]
    [InlineData("LastRun/ChangedFiles", "[null]")]
    [InlineData("LastRun/FixedTests", "[null]")]
    [InlineData("LastRun/NewlyFailingTests", "[null]")]
    [InlineData("LastRun/FailureClassifications", "[null]")]
    [InlineData("LastRun/FailureClassifications/0/Flakiness/Outcomes", null)]
    [InlineData("LastRun/FailureClassifications/0/Flakiness/Outcomes", "[null]")]
    [InlineData("LastRun/FailureClassifications/0/Flakiness/OtherWorktrees", null)]
    [InlineData("LastRun/FailureClassifications/0/Flakiness/OtherWorktrees", "[null]")]
    public void Ignores_structurally_invalid_results_before_restore(string path, string? replacement)
    {
        using (var store = Open())
        {
            store.Schedule(Snapshot());
        }

        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        var parts = path.Split('/');
        var parent = json;
        foreach (var part in parts[..^1])
        {
            parent = parent is JsonArray array ? array[int.Parse(part)]! : parent[part]!;
        }

        if (replacement == "<missing>")
        {
            _ = parent.AsObject().Remove(parts[^1]);
        }
        else if (parent is JsonArray items)
        {
            items[int.Parse(parts[^1])] = replacement is null ? null : JsonNode.Parse(replacement);
        }
        else
        {
            parent[parts[^1]] = replacement is null ? null : JsonNode.Parse(replacement);
        }
        File.WriteAllText(FilePath, json.ToJsonString());

        using var reader = Open();
        Assert.Null(reader.TryLoad());
        Assert.Contains(_messages, m => m.Contains("saved results", StringComparison.Ordinal)
            && (m.Contains("Ignoring", StringComparison.Ordinal) || m.Contains("Cannot read", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Debounces_and_saves_scheduled_results()
    {
        using var store = Open();
        store.SaveDelay = TimeSpan.FromMilliseconds(20);
        store.Schedule(Snapshot());

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!File.Exists(FilePath) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, _ct);
        }

        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void Clips_test_text_and_caps_build_diagnostics()
    {
        var snapshot = Snapshot() with
        {
            TestSuites =
            [
                Suite with
                {
                    Tests =
                    [
                        Result with
                        {
                            Output = new string('o', 5000),
                            ErrorMessage = new string('e', 9000),
                            StackTrace = new string('s', 9000),
                            Attempts = [new(TestStatus.Failed, TimeSpan.FromMilliseconds(10),
                                new string('e', 9000), new string('s', 9000))],
                        },
                    ],
                },
            ],
            LastBuild = new BuildResult(BuildStatus.Failed,
                Enumerable.Repeat(new string('b', 3000), 105).ToList(),
                Enumerable.Repeat("warning", 102).ToList(), TimeSpan.Zero)
            {
                OmittedErrors = 7,
                OmittedWarnings = 3,
            },
        };
        using (var store = Open())
        {
            store.Schedule(snapshot);
        }

        using var reader = Open();
        var restored = reader.TryLoad()!;
        var result = Assert.Single(Assert.Single(restored.TestSuites).Tests);
        Assert.EndsWith("[truncated]", result.Output);
        Assert.EndsWith("[truncated]", result.ErrorMessage);
        Assert.EndsWith("[truncated]", result.StackTrace);
        var attempt = Assert.Single(result.Attempts);
        Assert.EndsWith("[truncated]", attempt.ErrorMessage);
        Assert.EndsWith("[truncated]", attempt.StackTrace);
        Assert.Equal(100, restored.LastBuild!.Errors.Count);
        Assert.EndsWith("[truncated]", restored.LastBuild.Errors[^1]);
        Assert.Equal(12, restored.LastBuild.OmittedErrors);
        Assert.Equal(100, restored.LastBuild.Warnings.Count);
        Assert.Equal(5, restored.LastBuild.OmittedWarnings);
    }

    [Fact]
    public void Ignores_corrupt_and_incompatible_files_with_diagnostics()
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ invalid");
        using var store = Open();

        Assert.Null(store.TryLoad());
        Assert.Contains(_messages, m => m.Contains("Cannot read saved results", StringComparison.Ordinal));

        store.Schedule(Snapshot());
        store.Dispose();
        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        json["EngineVersion"] = "different";
        File.WriteAllText(FilePath, json.ToJsonString());
        using var wrongVersion = Open();
        Assert.Null(wrongVersion.TryLoad());
        Assert.Contains(_messages, m => m.Contains("engine version", StringComparison.Ordinal));
    }

    [Fact]
    public void Ignores_files_for_a_different_solution_and_format_version()
    {
        using (var store = Open())
        {
            store.Schedule(Snapshot());
        }

        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        json["SolutionPath"] = Path.Combine(_root, "Other.slnx");
        File.WriteAllText(FilePath, json.ToJsonString());
        using var wrongSolution = Open();
        Assert.Null(wrongSolution.TryLoad());
        Assert.Contains(_messages, m => m.Contains("solution path does not match", StringComparison.Ordinal));

        json["SolutionPath"] = _solution;
        json["FormatVersion"] = 999;
        File.WriteAllText(FilePath, json.ToJsonString());
        using var wrongFormat = Open();
        Assert.Null(wrongFormat.TryLoad());
        Assert.Contains(_messages, m => m.Contains("unsupported format version", StringComparison.Ordinal));
    }

    [Fact]
    public void Clear_removes_saved_results_and_a_pending_save()
    {
        var store = Open();
        store.Schedule(Snapshot());
        store.Clear();
        store.Dispose();

        Assert.False(File.Exists(FilePath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private SavedResultsStore Open() => new(_solution, _messages.Add);

    private static SavedResultsSnapshot Snapshot()
    {
        var run = new TestRunRecord
        {
            RunId = "run-1",
            EngineInstanceId = "engine",
            Sequence = 1,
            Trigger = TestRunTrigger.Startup,
            Status = TestRunStatus.Completed,
            Freshness = ResultFreshness.Current,
            RequestedAt = DateTimeOffset.UnixEpoch,
            CompletedAt = DateTimeOffset.UnixEpoch,
            Build = new BuildResult(BuildStatus.Succeeded, [], [], TimeSpan.FromSeconds(1)),
            Context = new TestRunContext(
                "/App.slnx", ["App.Tests"], ["App.Tests"], null, null, null, false,
                "Debug", null, ".NET", "OS", "/"),
            Suites = [Suite],
            Projects =
            [
                new("App.Tests", ProjectRunStatus.Completed, 1)
                {
                    Resources = new(
                        new TestResourceNeeds("App.Tests", ["database"], false, ["declared"]),
                        new TestResourceWait(TestResourceWaitState.Acquired, ["database"], false,
                            [new("database", TestResourceBlockerKind.Held, 1, null, null, DateTimeOffset.UnixEpoch)],
                            0, 0, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                        []),
                },
            ],
            FailureClassifications =
            [
                new("App.Tests", Result.FullyQualifiedName, FailureKind.Flaky, "Passed on retry",
                    new FlakinessEvidence(null, 1, 1, 1, ["other-worktree"],
                        [new("run-1", DateTimeOffset.UnixEpoch, TestStatus.Passed, 1, null)])),
            ],
        };
        return new SavedResultsSnapshot(
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            new BuildResult(BuildStatus.Succeeded, [], [], TimeSpan.FromSeconds(1)),
            [Suite],
            run);
    }

    private static TestSuite Suite => new("App.Tests", [Result], DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(20))
    {
        ProjectId = "tests/App.Tests/App.Tests.csproj",
    };

    private static TestResult Result => new(
        "App.Tests.MathTests.Adds", "Adds", TestStatus.Passed, TimeSpan.FromMilliseconds(20), null, null, null, null)
    {
        Attempts = [new(TestStatus.Failed, TimeSpan.FromMilliseconds(10), "first attempt failed", "stack")],
    };
}
