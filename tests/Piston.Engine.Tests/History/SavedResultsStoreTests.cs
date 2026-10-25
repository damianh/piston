// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Nodes;
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
        Assert.Equal(Assert.Single(expectedSuite.Tests), Assert.Single(restoredSuite.Tests));
        Assert.Equal(expected.LastRun?.RunId, restored.LastRun?.RunId);
        Assert.Equal(expected.LastBuild!.Status, restored.LastBuild!.Status);
        Assert.Equal(expected.LastBuild.Duration, restored.LastBuild.Duration);
        Assert.Equal(expected.LastBuild.Errors, restored.LastBuild.Errors);
        Assert.Equal(expected.LastTestDuration, restored.LastTestDuration);
        Assert.True(File.Exists(FilePath));
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
            Suites = [Suite],
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

    private static TestSuite Suite => new("App.Tests", [Result], DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(20));

    private static TestResult Result => new(
        "App.Tests.MathTests.Adds", "Adds", TestStatus.Passed, TimeSpan.FromMilliseconds(20), null, null, null, null);
}
