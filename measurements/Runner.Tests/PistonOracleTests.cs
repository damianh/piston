// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Piston.Measurements;
using Xunit;

namespace Piston.Measurements.Tests;

public sealed class PistonOracleTests
{
    private const string Source = "1111111111111111111111111111111111111111111111111111111111111111";

    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["Ns.A.First"] = "Passed",
        ["Ns.A.Second"] = "Failed",
        ["Ns.B.Skipped"] = "NotExecuted",
    };

    private static JsonObject ValidRun(string runId = "run-2") => new()
    {
        ["schema"] = "piston.test-run/v1",
        ["runId"] = runId,
        ["status"] = "Completed",
        ["isTerminal"] = true,
        ["scope"] = "Full",
        ["isFiltered"] = false,
        ["isFullScope"] = true,
        ["freshness"] = "Current",
        ["isCurrentFullResult"] = true,
        ["sourceChangeObserved"] = false,
        ["sourceBefore"] = new JsonObject { ["algorithm"] = PistonSourceFingerprint.Algorithm, ["value"] = Source },
        ["sourceAfter"] = new JsonObject { ["algorithm"] = PistonSourceFingerprint.Algorithm, ["value"] = Source },
        ["context"] = new JsonObject { ["effectiveFilter"] = null },
        ["summary"] = new JsonObject { ["total"] = 3, ["passed"] = 1, ["failed"] = 1, ["skipped"] = 1 },
        ["duplicateTestIds"] = new JsonArray(),
        ["tests"] = new JsonArray(
            Test("Ns.A.First", "Passed"), Test("Ns.A.Second", "Failed"), Test("Ns.B.Skipped", "Skipped")),
    };

    private static JsonObject Test(string name, string status) =>
        new() { ["fullyQualifiedName"] = name, ["status"] = status };

    private static void Rejected(JsonObject run, string reason, string runId = "run-2", string source = Source)
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            PistonRunOracle.Validate(run.ToJsonString(), runId, source, Expected));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void AcceptsCompleteCurrentFullScopeRun()
    {
        var observed = PistonRunOracle.Validate(ValidRun().ToJsonString(), "run-2", Source, Expected);
        Assert.Equal(Expected, observed);
    }

    [Fact]
    public void RejectsPreviouslyAccumulatedRun() => Rejected(ValidRun("run-1"), "not the requested run");

    [Fact]
    public void RejectsMissingExpectedRunId() => Rejected(ValidRun(), "expected run ID is missing", runId: "");

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Incomplete")]
    [InlineData("BuildFailed")]
    [InlineData("Faulted")]
    public void RejectsNonCompletedRuns(string status)
    {
        var run = ValidRun();
        run["status"] = status;
        Rejected(run, "run status is " + status);
    }

    [Fact]
    public void RejectsUnfinishedRun()
    {
        var run = ValidRun();
        run["isTerminal"] = false;
        Rejected(run, "has not finished");
    }

    [Fact]
    public void RejectsSelectiveScope()
    {
        var run = ValidRun();
        run["scope"] = "Selective";
        Rejected(run, "not Full");
    }

    [Fact]
    public void RejectsFilteredRun()
    {
        var run = ValidRun();
        run["isFiltered"] = true;
        run["context"] = new JsonObject { ["effectiveFilter"] = "FullyQualifiedName~A" };
        Rejected(run, "filtered");
    }

    [Fact]
    public void RejectsStaleRun()
    {
        var run = ValidRun();
        run["freshness"] = "Stale";
        Rejected(run, "freshness is Stale");
    }

    [Fact]
    public void RejectsObservedSourceChange()
    {
        var run = ValidRun();
        run["sourceChangeObserved"] = true;
        Rejected(run, "source change was observed");
    }

    [Fact]
    public void RejectsSourceGenerationMismatch() =>
        Rejected(ValidRun(), "does not match expected source", source: new string('2', 64));

    [Fact]
    public void RejectsMissingFingerprint()
    {
        var run = ValidRun();
        run.Remove("sourceAfter");
        Rejected(run, "sourceAfter fingerprint is missing");
    }

    [Fact]
    public void RejectsSummaryOnlyResults()
    {
        var run = ValidRun();
        run.Remove("tests");
        Rejected(run, "summary counts are not provenance");
    }

    [Fact]
    public void RejectsReportedDuplicates()
    {
        var run = ValidRun();
        run["duplicateTestIds"] = new JsonArray("Ns.A.First");
        Rejected(run, "duplicate test identities");
    }

    [Fact]
    public void RejectsDuplicateIdentitiesInTestList()
    {
        var run = ValidRun();
        ((JsonArray)run["tests"]!).Add(Test("Ns.A.First", "Passed"));
        Rejected(run, "duplicate test identity: Ns.A.First");
    }

    [Fact]
    public void RejectsMissingTest()
    {
        var run = ValidRun();
        ((JsonArray)run["tests"]!).RemoveAt(2);
        Rejected(run, "missing, e.g. Ns.B.Skipped");
    }

    [Fact]
    public void RejectsUnexpectedTest()
    {
        var run = ValidRun();
        ((JsonArray)run["tests"]!).Add(Test("Ns.C.Extra", "Passed"));
        Rejected(run, "unexpected test(s), e.g. Ns.C.Extra");
    }

    [Fact]
    public void RejectsStatusMismatch()
    {
        var run = ValidRun();
        run["tests"]![1]!["status"] = "Passed";
        Rejected(run, "Ns.A.Second expected Failed got Passed");
    }

    [Fact]
    public void RejectsMissingIdentityAndUnsupportedStatus()
    {
        var run = ValidRun();
        run["tests"]![0]!["fullyQualifiedName"] = "";
        Rejected(run, "test identity is missing");

        run = ValidRun();
        run["tests"]![0]!["status"] = "Running";
        Rejected(run, "unsupported status Running");
    }

    [Fact]
    public void RejectsWhenPistonDoesNotClaimCurrentFullResult()
    {
        var run = ValidRun();
        run["isCurrentFullResult"] = false;
        Rejected(run, "not mark the run as a current full result");
    }

    [Fact]
    public void RejectsNonJson() =>
        Assert.Throws<InvalidDataException>(() => PistonRunOracle.Validate("Phase: Watching, Passed: 3", "run-2", Source, Expected));

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"run\"")]
    [InlineData("42")]
    [InlineData("true")]
    public void RejectsNonObjectJsonRoots(string json) =>
        Assert.Throws<InvalidDataException>(() => PistonRunOracle.Validate(json, "run-2", Source, Expected));

    [Theory]
    [InlineData("context", "[]")]
    [InlineData("tests", "[42]")]
    [InlineData("tests", "[{\"fullyQualifiedName\":42,\"status\":\"Passed\"}]")]
    [InlineData("tests", "[{\"fullyQualifiedName\":\"Ns.A.First\",\"status\":42}]")]
    public void RejectsMalformedNestedShapes(string field, string json)
    {
        var run = ValidRun();
        run[field] = JsonNode.Parse(json);
        Rejected(run, "Piston run rejected:");
    }

    [Fact]
    public void FingerprintMatchesPinnedVectorShared_WithEngine()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "vector");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "A");
        File.WriteAllText(Path.Combine(root, "src", "b.cs"), "B");
        // Generated/tool directories are excluded.
        Directory.CreateDirectory(Path.Combine(root, "src", "obj"));
        File.WriteAllText(Path.Combine(root, "src", "obj", "x.json"), "X");
        Directory.CreateDirectory(Path.Combine(root, ".piston"));
        File.WriteAllText(Path.Combine(root, ".piston", "piston.db"), "D");

        const string expected = "47a023661287f6ce3402c175f1b8adf87ecde9245e70d0dda884a87b0e3ee16a";
        Assert.Equal(expected, PistonSourceFingerprint.Compute(root));
    }

    [Fact]
    public void FingerprintEmbeddedNulsCannotImpersonateFileBoundaries()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "nul-vector");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a"), "X\0b\0Y");
        var single = PistonSourceFingerprint.Compute(root);
        File.WriteAllText(Path.Combine(root, "a"), "X");
        File.WriteAllText(Path.Combine(root, "b"), "Y");

        Assert.NotEqual(single, PistonSourceFingerprint.Compute(root));
    }

    [Fact]
    public void FingerprintIncludesAncestorBuildInputs()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "unit");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "App.slnx"), "<Solution />");
        var before = PistonSourceFingerprint.ForSolution(Path.Combine(root, "App.slnx"));

        File.WriteAllText(Path.Combine(temp.Path, "Directory.Packages.props"), "<Project />");

        Assert.NotEqual(before, PistonSourceFingerprint.ForSolution(Path.Combine(root, "App.slnx")));
    }
}

public sealed class PistonCompileOracleTests
{
    private const string Source = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Solution = "/workspace/unit/Orders.slnx";
    private static readonly CompileDiagnostic Expected = new("CS0103", "Pricing/PriceRules.cs", 15);

    private static JsonObject ValidRun() => new()
    {
        ["schema"] = PistonRunOracle.Schema, ["runId"] = "forced-run", ["trigger"] = "Forced",
        ["status"] = "BuildFailed", ["isTerminal"] = true,
        ["scope"] = "Full", ["isFiltered"] = false, ["isFullScope"] = true,
        ["freshness"] = "Current", ["sourceChangeObserved"] = false, ["isCurrentFullResult"] = false,
        ["sourceBefore"] = new JsonObject { ["algorithm"] = PistonSourceFingerprint.Algorithm, ["value"] = Source },
        ["sourceAfter"] = new JsonObject { ["algorithm"] = PistonSourceFingerprint.Algorithm, ["value"] = Source },
        ["context"] = new JsonObject { ["solutionPath"] = Solution, ["effectiveFilter"] = null, ["buildTargets"] = null },
        ["build"] = new JsonObject { ["status"] = "Failed",
            ["errors"] = new JsonArray("/workspace/unit/Pricing/PriceRules.cs(15,9): error CS0103: MissingSymbol [Pricing.csproj]") },
        ["tests"] = new JsonArray(), ["duplicateTestIds"] = new JsonArray(),
    };

    private static void Validate(JsonObject run) => PistonRunOracle.ValidateCompileDiagnostic(
        run.ToJsonString(), "forced-run", Source, Expected, Solution);

    [Fact]
    public void AcceptsOnlyRunLocalCurrentCompileEvidenceNotCurrentTestResults()
    {
        Validate(ValidRun());
        Assert.Throws<InvalidDataException>(() => PistonRunOracle.Validate(
            ValidRun().ToJsonString(), "forced-run", Source, new Dictionary<string, string>(), Solution));
    }

    [Theory]
    [InlineData("schema", "\"unknown\"")]
    [InlineData("runId", "\"old-run\"")]
    [InlineData("trigger", "\"FileChange\"")]
    [InlineData("trigger", "\"Startup\"")]
    [InlineData("status", "\"Completed\"")]
    [InlineData("status", "\"Cancelled\"")]
    [InlineData("status", "\"Faulted\"")]
    [InlineData("status", "\"Incomplete\"")]
    [InlineData("isTerminal", "false")]
    [InlineData("scope", "\"Selective\"")]
    [InlineData("isFiltered", "true")]
    [InlineData("isFullScope", "false")]
    [InlineData("isCurrentFullResult", "true")]
    [InlineData("isCurrentFullResult", "null")]
    [InlineData("isCurrentFullResult", "\"false\"")]
    [InlineData("isCurrentFullResult", "0")]
    [InlineData("freshness", "\"Stale\"")]
    [InlineData("freshness", "\"Unknown\"")]
    [InlineData("sourceChangeObserved", "true")]
    [InlineData("sourceBefore", "null")]
    [InlineData("sourceAfter", "{}")]
    [InlineData("sourceAfter", "{\"algorithm\":\"piston-source-v1\",\"value\":\"old\"}")]
    [InlineData("sourceAfter", "{\"algorithm\":\"piston-source-v2\",\"value\":\"old\"}")]
    [InlineData("context", "{\"solutionPath\":\"/different/unit/Orders.slnx\"}")]
    [InlineData("context", "{\"solutionPath\":\"/workspace/unit/Orders.slnx\",\"effectiveFilter\":\"A\"}")]
    [InlineData("context", "{\"solutionPath\":\"/workspace/unit/Orders.slnx\",\"buildTargets\":[\"Pricing.csproj\"]}")]
    [InlineData("build", "null")]
    [InlineData("build", "{\"status\":\"Succeeded\",\"errors\":[]}")]
    [InlineData("build", "{\"status\":\"Failed\"}")]
    [InlineData("build", "{\"status\":\"Failed\",\"errors\":[]}")]
    [InlineData("build", "{\"status\":\"Failed\",\"errors\":[42]}")]
    [InlineData("build", "{\"status\":\"Failed\",\"warnings\":[\"CS0103\"]}")]
    [InlineData("tests", "null")]
    [InlineData("tests", "[{\"fullyQualifiedName\":\"OldTest\",\"status\":\"Passed\"}]")]
    [InlineData("duplicateTestIds", "[\"OldTest\"]")]
    public void RejectsInvalidProvenanceAndMissingDiagnostics(string key, string json)
    {
        var run = ValidRun();
        run[key] = JsonNode.Parse(json);
        Assert.Throws<InvalidDataException>(() => Validate(run));
    }

    [Fact]
    public void RejectsMissingCurrentFullResultClaim()
    {
        var run = ValidRun();
        run.Remove("isCurrentFullResult");
        Assert.Throws<InvalidDataException>(() => Validate(run));
    }

    [Theory]
    [InlineData("/workspace/unit/Pricing/PriceRules.cs(15,9): error CS0104: wrong ID")]
    [InlineData("/workspace/unit/Pricing/Other.cs(15,9): error CS0103: wrong file")]
    [InlineData("/workspace/other/Pricing/PriceRules.cs(15,9): error CS0103: wrong workspace")]
    [InlineData("/workspace/unit/Pricing/PriceRules.cs(16,9): error CS0103: wrong line")]
    [InlineData("/workspace/unit/Pricing/PriceRules.cs(15,9): warning CS0103: not an error")]
    [InlineData("error CS0103: no stable location")]
    [InlineData("CS0103 at PriceRules.cs:15")]
    public void RejectsWrongIdFileLineOrSeverity(string error)
    {
        var run = ValidRun();
        run["build"]!["errors"] = new JsonArray(error);
        Assert.Throws<InvalidDataException>(() => Validate(run));
        Assert.False(Expected.Matches(error, Solution));
    }

    [Fact]
    public void MatchesSolutionRelativeLocationAndRepeatedMsbuildSummary()
    {
        const string error = "Pricing/PriceRules.cs(15,9): error CS0103: MissingSymbol";
        Assert.True(Expected.Matches(error + "\n" + error, Solution));
        var run = ValidRun();
        run["build"]!["errors"] = new JsonArray(error, error);
        Validate(run);
    }
}

/// <summary>Skips unless <c>PISTON_DAEMON_DLL</c> points at a built <c>piston.dll</c>.</summary>
public sealed class PistonDaemonFactAttribute : FactAttribute
{
    public PistonDaemonFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PISTON_DAEMON_DLL")))
            Skip = "Set PISTON_DAEMON_DLL to a built piston.dll to run the end-to-end Piston oracle smoke.";
    }
}

/// <summary>
/// Opt-in end-to-end smoke against a real Piston daemon's HTTP MCP endpoint using the unit fixture.
/// Performs one plain baseline run plus a handful of Piston runs; it is not a measurement campaign.
/// </summary>
public sealed class PistonOracleSmokeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    [PistonDaemonFact]
    public async Task PistonFullScopeRunsAreBoundToTheRequestedSourceGeneration()
    {
        var root = Program.FindRoot();
        var workload = FixtureManifest.Load(Path.Combine(root, "scenarios.json")).Workloads["unit"];
        var regression = workload.Scenarios["regression"];
        using var temp = new TemporaryDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        var artifacts = Path.Combine(temp.Path, "artifacts");
        Directory.CreateDirectory(artifacts);
        Evidence.CopyFixture(Path.Combine(root, "fixtures"), workspace);
        var solution = Path.Combine(workspace, workload.Solution);
        var source = Path.Combine(workspace, workload.Source);

        // Plain dotnet establishes the complete expected identity set.
        Assert.Equal(0, (await Processes.Command(["dotnet", "restore", solution, "--locked-mode", "--verbosity", "quiet"],
            workspace, artifacts, "restore", Timeout.TotalSeconds)).ExitCode);
        var baselineDirectory = Path.Combine(artifacts, "baseline");
        Directory.CreateDirectory(baselineDirectory);
        var baselineRun = await Processes.Command(Processes.TestCommand(solution, baselineDirectory),
            workspace, artifacts, "baseline", Timeout.TotalSeconds);
        var baseline = Evidence.ParseTrx(baselineDirectory, baselineRun.StartedWallNs);
        Assert.True(baseline.Count >= workload.MinimumTests);
        Evidence.Oracle(baseline.ToDictionary(pair => pair.Key, _ => "Passed", StringComparer.Ordinal),
            baseline, baselineRun.ExitCode);
        var cleanSource = PistonSourceFingerprint.ForSolution(solution);

        var mcpPort = FreePort();
        using var daemon = StartDaemon(workspace, solution, mcpPort, FreePort(), artifacts);
        try
        {
            using var mcp = new PistonMcpClient(new Uri($"http://localhost:{mcpPort}/"));
            await WaitForMcpAsync(mcp, daemon);

            // The automatic startup run must finish before the measured request so it cannot be reused.
            var startup = await WaitForRunAsync(mcp, run => run["trigger"]?.GetValue<string>() == "Startup"
                && run["isTerminal"]?.GetValue<bool>() == true);

            // 1) A requested full run matches the complete expected identity/status set and fingerprint.
            var cleanRunId = RunIdFrom(await mcp.CallToolAsync("run_tests"));
            var cleanRun = await mcp.CallToolAsync("get_test_run", new JsonObject { ["runId"] = cleanRunId });
            PistonRunOracle.Validate(cleanRun, cleanRunId, cleanSource, baseline);

            // 4) A previously accumulated result cannot be mistaken for this run.
            var startupId = startup["runId"]!.GetValue<string>();
            Assert.NotEqual(startupId, cleanRunId);
            Assert.Contains("not the requested run",
                Assert.Throws<InvalidDataException>(() =>
                    PistonRunOracle.Validate(startup.ToJsonString(), cleanRunId, cleanSource, baseline)).Message);

            // 2a) Mutating source makes the earlier run's generation mismatch the requested one.
            File.WriteAllText(source, File.ReadAllText(source).Replace(regression.Before, regression.After, StringComparison.Ordinal));
            var regressionSource = PistonSourceFingerprint.ForSolution(solution);
            var regressionExpected = Evidence.ExpectedResults(baseline, regression.FailedSuffixes);
            Assert.Contains("does not match expected source",
                Assert.Throws<InvalidDataException>(() =>
                    PistonRunOracle.Validate(cleanRun, cleanRunId, regressionSource, regressionExpected)).Message);

            // 3a) The watcher-triggered selective run cannot satisfy the full-scope oracle.
            var fileChangeRun = await WaitForRunAsync(mcp, run => run["trigger"]?.GetValue<string>() == "FileChange"
                && run["isTerminal"]?.GetValue<bool>() == true);
            Assert.Throws<InvalidDataException>(() => PistonRunOracle.Validate(fileChangeRun.ToJsonString(),
                fileChangeRun["runId"]!.GetValue<string>(), regressionSource, regressionExpected));

            // A fresh full run for the edited generation reports the expected regression failures.
            var regressionRunId = RunIdFrom(await mcp.CallToolAsync("run_tests"));
            PistonRunOracle.Validate(await mcp.CallToolAsync("get_test_run", new JsonObject { ["runId"] = regressionRunId }),
                regressionRunId, regressionSource, regressionExpected);

            // 3b) A user filter makes even a forced full run ineligible.
            await mcp.CallToolAsync("set_test_filter", new JsonObject { ["filter"] = "FullyQualifiedName~PriceTests" });
            var filteredRunId = RunIdFrom(await mcp.CallToolAsync("run_tests"));
            var filteredRun = await mcp.CallToolAsync("get_test_run", new JsonObject { ["runId"] = filteredRunId });
            var filteredDoc = JsonNode.Parse(filteredRun)!;
            Assert.True(filteredDoc["isFiltered"]!.GetValue<bool>(), filteredRun);
            Assert.False(filteredDoc["isFullScope"]!.GetValue<bool>(), filteredRun);
            Assert.Contains("filtered", Assert.Throws<InvalidDataException>(() => PistonRunOracle.Validate(
                filteredRun, filteredRunId, regressionSource, regressionExpected)).Message);
            await mcp.CallToolAsync("set_test_filter", new JsonObject { ["filter"] = "" });

            // 2b) Mutating source while a requested run is executing yields a stale or cancelled run.
            var midRun = mcp.CallToolAsync("run_tests");
            var active = await WaitForRunAsync(mcp, run => run["trigger"]?.GetValue<string>() == "Forced"
                && run["status"]?.GetValue<string>() == "Running", "active");
            File.WriteAllText(source, File.ReadAllText(source).Replace(regression.After, regression.Before, StringComparison.Ordinal));
            var midRunId = RunIdFrom(await midRun);
            Assert.Equal(active["runId"]!.GetValue<string>(), midRunId);
            var midRunJson = await mcp.CallToolAsync("get_test_run", new JsonObject { ["runId"] = midRunId });
            var midRunDoc = JsonNode.Parse(midRunJson)!;
            Assert.False(midRunDoc["isCurrentFullResult"]!.GetValue<bool>());
            Assert.True(midRunDoc["freshness"]!.GetValue<string>() == "Stale"
                || midRunDoc["status"]!.GetValue<string>() == "Cancelled", midRunJson);
            Assert.Throws<InvalidDataException>(() => PistonRunOracle.Validate(midRunJson, midRunId,
                PistonSourceFingerprint.ForSolution(solution), baseline));
        }
        finally
        {
            StopDaemon(daemon);
        }
    }

    private static string RunIdFrom(string runTestsOutput) =>
        Regex.Match(runTestsOutput, @"Run: ([0-9a-f]{32}) ") is { Success: true } match
            ? match.Groups[1].Value
            : throw new InvalidDataException("RunTests did not report a run ID: " + runTestsOutput);

    private static async Task<JsonObject> WaitForRunAsync(PistonMcpClient mcp, Func<JsonObject, bool> predicate,
        string runId = "latest")
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (JsonNode.Parse(await mcp.CallToolAsync("get_test_run", new JsonObject { ["runId"] = runId })) is JsonObject run
                    && predicate(run))
                    return run;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("No test run", StringComparison.Ordinal))
            {
                // No matching run yet.
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Timed out waiting for Piston run state");
    }

    private static async Task WaitForMcpAsync(PistonMcpClient mcp, Process daemon)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            if (daemon.HasExited) throw new InvalidOperationException($"Piston daemon exited with {daemon.ExitCode}");
            try
            {
                await mcp.InitializeAsync();
                return;
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }

    private static Process StartDaemon(string workspace, string solution, int mcpPort, int webPort, string artifacts)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     Environment.GetEnvironmentVariable("PISTON_DAEMON_DLL")!, "daemon", solution, "--no-stdio",
                     "--mcp-port", mcpPort.ToString(), "--web-port", webPort.ToString(),
                     "--pipe-name", "piston-oracle-" + Guid.NewGuid().ToString("N"),
                     "--config-directory", Path.Combine(artifacts, "config"),
                 })
            info.ArgumentList.Add(argument);
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        var process = Process.Start(info) ?? throw new InvalidOperationException("Failed to start Piston daemon");
        var log = new StreamWriter(Path.Combine(artifacts, "piston-daemon.log")) { AutoFlush = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        process.Exited += (_, _) => { lock (log) log.Dispose(); };
        process.EnableRaisingEvents = true;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void StopDaemon(Process daemon)
    {
        if (daemon.HasExited) return;
        daemon.Kill(entireProcessTree: true);
        daemon.WaitForExit(10_000);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
