using System.Text.Json.Nodes;
using Piston.Measurements;
using Xunit;

namespace Piston.Measurements.Tests;

public sealed class PistonTrialTests
{
    [Theory]
    [InlineData("baseline")]
    [InlineData("regression")]
    [InlineData("rounding")]
    [InlineData("known-failure")]
    [InlineData("regression-with-existing-failure")]
    [InlineData("repair")]
    public async Task AcceptsOnlyOracleValidatedPistonTrials(string scenario)
    {
        var (row, fake) = await Run(scenario);
        Assert.True(row.Correct, row.Error);
        Assert.Equal("piston", row.Condition);
        Assert.Equal("piston-generation-bound-full-test-scope", row.OracleKind);
        Assert.Equal(row.ExpectedTests, row.ObservedTests);
        Assert.NotNull(row.PistonSourceFingerprint);
        Assert.NotNull(row.PistonExecution);
        Assert.Null(row.Execution);
        Assert.True(row.EditToCorrectResultMs >= 0);
        Assert.True(fake.Prepared);
        Assert.True(fake.Disposed);
    }

    [Theory]
    [InlineData("runId", "\"previous-run\"")]
    [InlineData("status", "\"Faulted\"")]
    [InlineData("status", "\"BuildFailed\"")]
    [InlineData("status", "\"Cancelled\"")]
    [InlineData("status", "\"Incomplete\"")]
    [InlineData("isTerminal", "false")]
    [InlineData("scope", "\"Selective\"")]
    [InlineData("isFiltered", "true")]
    [InlineData("freshness", "\"Stale\"")]
    [InlineData("sourceChangeObserved", "true")]
    [InlineData("sourceAfter", "{}")]
    [InlineData("context", "{\"solutionPath\":\"/other/workspace/Orders.slnx\",\"effectiveFilter\":null}")]
    [InlineData("tests", "null")]
    [InlineData("tests", "[]")]
    [InlineData("tests", "[{\"fullyQualifiedName\":\"Extra\",\"status\":\"Passed\"}]")]
    public async Task RejectedRunsArePersistedAsInvalidAttempts(string field, string json)
    {
        var (row, fake) = await Run("regression", run => run[field] = JsonNode.Parse(json));
        Assert.False(row.Correct);
        Assert.Null(row.EditToCorrectResultMs);
        Assert.Contains("Piston run rejected:", row.Error);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task StatusMismatchAndSourceMutationCannotBecomeValidTrials()
    {
        var (mismatch, _) = await Run("regression", run => run["tests"]![0]!["status"] = "Failed");
        Assert.False(mismatch.Correct);
        Assert.Contains("status mismatch", mismatch.Error);
        var (changed, _) = await Run("baseline", mutateSource: true);
        Assert.False(changed.Correct);
        Assert.Contains("Source changed", changed.Error);
        Assert.Null(changed.EditToCorrectResultMs);
    }

    [Fact]
    public async Task TimeoutAndMcpErrorsRemainInvalidAndDisposeDaemon()
    {
        var (timedOut, timeoutFake) = await Run("baseline", timeout: true);
        Assert.False(timedOut.Correct);
        Assert.True(timedOut.TimedOut);
        Assert.Null(timedOut.EditToCorrectResultMs);
        Assert.True(timeoutFake.Disposed);
        var (failed, failedFake) = await Run("baseline", fail: true);
        Assert.False(failed.Correct);
        Assert.Contains("MCP failure", failed.Error);
        Assert.True(failedFake.Disposed);
    }

    [Fact]
    public async Task CleanupFailureInvalidatesPreviouslyAcceptedTrial()
    {
        var (row, _) = await Run("baseline", cleanupFailure: true);
        Assert.False(row.Correct);
        Assert.Null(row.EditToCorrectResultMs);
        Assert.Contains("Piston cleanup failed", row.Error);
        Assert.Single(row.Incidents);
    }

    [Theory]
    [InlineData("--condition", "unknown")]
    [InlineData("--condition", "piston")]
    [InlineData("--piston-daemon-dll", "not-a-daemon.dll")]
    public async Task InvalidAdapterOptionsFailBeforeCreatingArtifacts(string option, string value)
    {
        using var temp = new TemporaryDirectory();
        var output = Path.Combine(temp.Path, "output");
        Assert.Equal(2, await Program.Main(["--output", output, option, value]));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("integration", "regression")]
    public async Task UnsupportedScopesFailBeforeCreatingArtifacts(string workload, string scenario)
    {
        using var temp = new TemporaryDirectory();
        var output = Path.Combine(temp.Path, "output");
        Assert.Equal(2, await Program.Main(["--output", output, "--condition", "piston",
            "--piston-daemon-dll", typeof(Program).Assembly.Location, "--workload", workload, "--scenario", scenario]));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task CompileDiagnosticTrialUsesBuildOracleWithoutTestCounts()
    {
        var (row, fake) = await Run("compile-error");
        Assert.True(row.Correct, row.Error);
        Assert.Equal("piston-generation-bound-compile-diagnostic", row.OracleKind);
        Assert.Equal(new CompileDiagnostic("CS0103", "Pricing/PriceRules.cs", 15), row.ExpectedDiagnostic);
        Assert.Null(row.ExpectedTests);
        Assert.Null(row.ObservedTests);
        Assert.True(fake.Disposed);
    }

    [Theory]
    [InlineData("build", "null")]
    [InlineData("freshness", "\"Stale\"")]
    [InlineData("runId", "\"previous-run\"")]
    public async Task InvalidCompileTrialsAreRetained(string field, string json)
    {
        var (row, fake) = await Run("compile-error", run => run[field] = JsonNode.Parse(json));
        Assert.False(row.Correct);
        Assert.Null(row.EditToCorrectResultMs);
        Assert.Contains("Piston run rejected:", row.Error);
        Assert.True(fake.Disposed);
    }

    private static async Task<(Trial Row, FakePiston Fake)> Run(string scenarioName,
        Action<JsonObject>? corrupt = null, bool mutateSource = false, bool timeout = false,
        bool fail = false, bool cleanupFailure = false)
    {
        using var temp = new TemporaryDirectory();
        var root = Program.FindRoot();
        var workload = FixtureManifest.Load(Path.Combine(root, "scenarios.json")).Workloads["unit"];
        var scenario = workload.Scenarios[scenarioName];
        var clean = new Dictionary<string, string> { ["Tests.Case0"] = "Passed" };
        foreach (var suffix in workload.Scenarios.Values.SelectMany(s => s.FailedSuffixes)
                     .Concat(workload.Scenarios.Values.SelectMany(s => s.BaselineFailedSuffixes)).Distinct())
            clean["Tests." + suffix] = "Passed";
        for (var i = clean.Count; i < workload.MinimumTests; i++) clean["Tests.Case" + i] = "Passed";
        var baseline = Evidence.ExpectedResults(clean, scenario.BaselineFailedSuffixes);
        var expected = Evidence.ExpectedResults(baseline, scenario.FailedSuffixes);
        Task<CommandResult> Execute(string[] argv, string cwd, string artifacts, string label,
            double seconds, Dictionary<string, string>? environment, CancellationToken token)
        {
            Assert.NotEqual("edited", label);
            var tests = label == "repair-preparation" ?
                Evidence.ExpectedResults(baseline, workload.Scenarios["regression"].FailedSuffixes) : baseline;
            if (label != "restore") HarnessTests.WriteTrx(argv[Array.IndexOf(argv, "--results-directory") + 1], tests);
            return Task.FromResult(new CommandResult(argv, 1, tests.Values.Contains("Failed") && label != "restore" ? 1 : 0,
                false, 1, 0, label + ".log", 0, 0));
        }
        FakePiston? fake = null;
        var options = new Options("unit", scenarioName, temp.Path, Timeout: timeout ? .05 : 5,
            Condition: "piston", PistonDaemonDll: "fake.dll");
        var row = await Trials.Run(root, options, workload, "batch", 0, new EditBarrier(1), execute: Execute,
            startPiston: launch =>
                fake = new FakePiston(launch.Solution, Path.Combine(launch.Workspace, workload.Source), expected,
                    scenario.Repair ? Evidence.ExpectedResults(baseline, workload.Scenarios["regression"].FailedSuffixes) : baseline,
                    corrupt, mutateSource, timeout, fail, cleanupFailure, diagnostic: scenario.Diagnostic));
        var persisted = JsonNode.Parse(File.ReadAllText(Path.Combine(row.ArtifactDirectory, "trial.json")))!;
        Assert.Equal(row.Correct, persisted["correct"]!.GetValue<bool>());
        Assert.Equal(row.Correct ? 1 : 0,
            ((Dictionary<string, object?>[])Evidence.Summarize([row])["groups"]!)[0]["valid"]);
        return (row, fake!);
    }

    internal sealed class FakePiston(string solution, string source, Dictionary<string, string> expected,
        Dictionary<string, string> preparation, Action<JsonObject>? corrupt, bool mutateSource,
        bool timeout, bool fail, bool cleanupFailure, int parallelism = 1, double delayMs = 0,
        string? diagnostic = null) : IPistonTrial
    {
        public bool Prepared { get; private set; }
        public bool Disposed { get; private set; }
        public Task Prepare(string fingerprint, IReadOnlyDictionary<string, string> tests, CancellationToken token)
        {
            Assert.Equal(PistonSourceFingerprint.ForSolution(solution), fingerprint);
            Assert.Equal(preparation, tests);
            Prepared = true;
            return Task.CompletedTask;
        }
        public async Task<PistonExecution> Run(string fingerprint, CancellationToken token)
        {
            Assert.True(Prepared);
            Assert.Equal(PistonSourceFingerprint.ForSolution(solution), fingerprint);
            if (timeout) await Task.Delay(Timeout.Infinite, token);
            if (fail) throw new InvalidOperationException("MCP failure");
            if (delayMs > 0) await Task.Delay(TimeSpan.FromMilliseconds(delayMs), token);
            var run = new JsonObject
            {
                ["schema"] = PistonRunOracle.Schema, ["runId"] = "requested-run", ["status"] = "Completed",
                ["isTerminal"] = true, ["scope"] = "Full", ["isFiltered"] = false, ["isFullScope"] = true,
                ["freshness"] = "Current", ["isCurrentFullResult"] = true, ["sourceChangeObserved"] = false,
                ["sourceBefore"] = new JsonObject { ["algorithm"] = PistonSourceFingerprint.Algorithm, ["value"] = fingerprint },
                ["sourceAfter"] = new JsonObject { ["algorithm"] = PistonSourceFingerprint.Algorithm, ["value"] = fingerprint },
                ["admission"] = new JsonObject { ["state"] = "Admitted", ["budgetSource"] = "default",
                    ["otherAdmittedRuns"] = 0, ["queuedAhead"] = 0, ["reservedCpuSlots"] = parallelism },
                ["context"] = new JsonObject { ["effectiveFilter"] = null,
                    ["solutionPath"] = Path.GetFullPath(solution) }, ["duplicateTestIds"] = new JsonArray(),
                ["tests"] = new JsonArray(expected.Select(pair => (JsonNode)new JsonObject
                    { ["fullyQualifiedName"] = pair.Key, ["status"] = pair.Value }).ToArray())
            };
            if (diagnostic is not null)
            {
                run["status"] = "BuildFailed";
                run["trigger"] = "Forced";
                run["isCurrentFullResult"] = false;
                run["tests"] = new JsonArray();
                run["build"] = new JsonObject
                {
                    ["status"] = "Failed",
                    ["errors"] = new JsonArray($"{source}(15,9): error {diagnostic}: MissingSymbol"),
                };
            }
            corrupt?.Invoke(run);
            if (mutateSource) File.AppendAllText(source, "\n// changed\n");
            return new("requested-run", run.ToJsonString(), 1, 42, 0, 0);
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (cleanupFailure) throw new IOException("cleanup failed");
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class PistonTrialSmokeTests
{
    [PistonDaemonFact]
    public async Task RealDaemonCompileDiagnosticTrialIsCurrentAndRunBound()
    {
        using var temp = new TemporaryDirectory();
        var root = Program.FindRoot();
        var options = new Options("unit", "compile-error", temp.Path, Timeout: 180,
            Condition: "piston", PistonDaemonDll: Path.GetFullPath(Environment.GetEnvironmentVariable("PISTON_DAEMON_DLL")!));
        var workload = options.Validate(FixtureManifest.Load(Path.Combine(root, "scenarios.json")));
        var row = await Trials.Run(root, options, workload, "compile-smoke", 0, new EditBarrier(1));
        Assert.True(row.Correct, row.Error);
        Assert.Equal("piston-generation-bound-compile-diagnostic", row.OracleKind);
        Assert.Equal(new CompileDiagnostic("CS0103", "Pricing/PriceRules.cs", 15), row.ExpectedDiagnostic);
        var run = JsonNode.Parse(row.PistonExecution!.RunJson)!;
        Assert.Equal("BuildFailed", run["status"]!.GetValue<string>());
        Assert.Equal("Current", run["freshness"]!.GetValue<string>());
        Assert.False(run["isCurrentFullResult"]!.GetValue<bool>());
        Assert.Empty((JsonArray)run["tests"]!);
        var solution = Path.Combine(row.ArtifactDirectory, "workspace", workload.Solution);
        Assert.Throws<InvalidDataException>(() => PistonRunOracle.ValidateCompileDiagnostic(
            row.PistonExecution.RunJson, "wrong-run", row.PistonSourceFingerprint!, row.ExpectedDiagnostic!, solution));
        Assert.Throws<InvalidDataException>(() => PistonRunOracle.ValidateCompileDiagnostic(
            row.PistonExecution.RunJson, row.PistonExecution.RunId, new string('0', 64), row.ExpectedDiagnostic!, solution));
        var startup = File.ReadAllText(Path.Combine(row.ArtifactDirectory, "piston-startup.json"));
        Assert.Throws<InvalidDataException>(() => PistonRunOracle.ValidateCompileDiagnostic(
            startup, row.PistonExecution.RunId, row.PistonSourceFingerprint!, row.ExpectedDiagnostic!, solution));
        var cleanup = JsonNode.Parse(File.ReadAllText(Path.Combine(row.ArtifactDirectory, "piston-cleanup.json")))!;
        Assert.True(cleanup["exited"]!.GetValue<bool>());
    }

    [PistonDaemonFact]
    public async Task RealDaemonTrialPreservesNewAndExistingFailures()
    {
        using var temp = new TemporaryDirectory();
        var root = Program.FindRoot();
        var options = new Options("unit", "regression-with-existing-failure", temp.Path, Timeout: 180,
            Condition: "piston", PistonDaemonDll: Path.GetFullPath(Environment.GetEnvironmentVariable("PISTON_DAEMON_DLL")!));
        var workload = options.Validate(FixtureManifest.Load(Path.Combine(root, "scenarios.json")));
        var row = await Trials.Run(root, options, workload, "smoke", 0, new EditBarrier(1));
        Assert.True(row.Correct, row.Error);
        Assert.Equal(409, row.ObservedTests!.Count);
        Assert.Equal(3, row.ObservedTests.Values.Count(status => status == "Failed"));
        Assert.Null(row.Execution);
        Assert.NotNull(row.PistonExecution);
        var cleanup = JsonNode.Parse(File.ReadAllText(Path.Combine(row.ArtifactDirectory, "piston-cleanup.json")))!;
        Assert.True(cleanup["exited"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Combine(row.ArtifactDirectory, "piston-run.json")));
    }
}
