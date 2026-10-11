// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Piston.Measurements;
using Xunit;

namespace Piston.Measurements.Tests;

public sealed class PairedTests
{
    private static string Root => Program.FindRoot();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(20)]
    public void ScheduleIsBalancedDeterministicAndRecordsPairIdentities(int pairs)
    {
        var plan = Paired.Schedule(pairs, 1234);
        Assert.Equal(plan.Select(pair => (pair.PairId, pair.Repetition, string.Join(",", pair.Order))),
            Paired.Schedule(pairs, 1234).Select(pair => (pair.PairId, pair.Repetition, string.Join(",", pair.Order))));
        Assert.Equal(Enumerable.Range(0, pairs), plan.Select(pair => pair.Repetition));
        Assert.Equal(pairs, plan.Select(pair => pair.PairId).Distinct().Count());
        Assert.All(plan, pair => Assert.Equal(new[] { "dotnet", "piston" }, pair.Order.Order()));
        var dotnetFirst = plan.Count(pair => pair.Order[0] == "dotnet");
        Assert.InRange(dotnetFirst, pairs / 2, (pairs + 1) / 2);
    }

    [Fact]
    public void ScheduleOrderDependsOnSeedAndRejectsEmptyPlans()
    {
        var orders = Enumerable.Range(0, 20).Select(seed =>
            string.Join("|", Paired.Schedule(6, seed).Select(pair => pair.Order[0]))).Distinct().Count();
        Assert.True(orders > 1);
        Assert.Throws<ArgumentException>(() => Paired.Schedule(0, 1));
    }

    [Fact]
    public async Task RunExecutesPlannedOrderSequentiallyAndStopsOnCancellation()
    {
        var plan = Paired.Schedule(4, 7);
        var calls = new List<(string Condition, string Batch, string Pair, int Position)>();
        var active = 0;
        var options = new Options("unit", "regression", "/tmp/x", 2, 4, ApproveCampaign: true, Paired: true);
        RunConditionBatch fake = async (batchOptions, batchId, pairId, position, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            await Task.Yield();
            calls.Add((batchOptions.Condition, batchId, pairId, position));
            Interlocked.Decrement(ref active);
            return [];
        };
        await Paired.Run(options, plan, fake, CancellationToken.None);
        Assert.Equal(plan.SelectMany(pair => pair.Order.Select((condition, position) =>
            (condition, $"{pair.PairId}-{condition}", pair.PairId, position))), calls);
        using var cancel = new CancellationTokenSource();
        calls.Clear();
        RunConditionBatch cancelling = (batchOptions, batchId, pairId, position, token) =>
        {
            calls.Add((batchOptions.Condition, batchId, pairId, position));
            cancel.Cancel();
            return Task.FromResult(Array.Empty<Trial>());
        };
        await Paired.Run(options, plan, cancelling, cancel.Token);
        Assert.Single(calls);
    }

    [Fact]
    public async Task EndToEndValidPairsProduceBatchLevelStatisticsAndPersistPairIdentity()
    {
        using var temp = new TemporaryDirectory();
        var options = new Options("unit", "regression", temp.Path, 2, 2, Timeout: 30, ApproveCampaign: true,
            PistonDaemonDll: "fake.dll", Paired: true);
        var plan = Paired.Schedule(2, 99);
        var launches = new ConcurrentBag<PistonLaunch>();
        var records = await Paired.Run(options, plan, FakeBatches(launches), CancellationToken.None);
        Assert.Equal(8, records.Count);
        Assert.All(records, row => Assert.True(row.Correct, row.Error));
        var analysis = Paired.Analyze(options, plan, records, 99, cancelled: false);
        Assert.True((bool)analysis["statistics_eligible"]!);
        var changes = Assert.IsType<Dictionary<string, object?>>(analysis["paired_changes"]);
        Assert.Equal(2, changes["paired_batches"]);
        Assert.Equal(99, changes["seed"]);
        Assert.NotNull(changes["bootstrap_95_percent_interval"]);
        Assert.Equal(4, launches.Count);
        Assert.All(launches, launch =>
        {
            Assert.Equal(1, launch.Parallelism);
            Assert.StartsWith(launch.Artifacts, launch.ExecutionDirectory, StringComparison.Ordinal);
        });
        Assert.Equal(4, launches.Select(launch => launch.ExecutionDirectory).Distinct().Count());
        foreach (var row in records)
        {
            var persisted = JsonNode.Parse(File.ReadAllText(Path.Combine(row.ArtifactDirectory, "trial.json")))!;
            Assert.Equal(row.PairId, persisted["pair_id"]!.GetValue<string>());
            Assert.Equal(row.PairPosition, persisted["pair_position"]!.GetValue<int>());
            Assert.Equal(row.TestProcessParallelism, persisted["test_process_parallelism"]!.GetValue<int>());
            var plannedPosition = Array.IndexOf(plan.Single(pair => pair.PairId == row.PairId).Order, row.Condition);
            Assert.Equal(plannedPosition, row.PairPosition);
        }
        Assert.All(records.Where(row => row.Condition == "piston"), row => Assert.True(row.PistonAdmissionIsolated));
    }

    [Fact]
    public async Task InvalidAttemptsAreRetainedAndWithholdStatistics()
    {
        using var temp = new TemporaryDirectory();
        var options = new Options("unit", "regression", temp.Path, 1, 2, Timeout: 30, ApproveCampaign: true,
            PistonDaemonDll: "fake.dll", Paired: true);
        var plan = Paired.Schedule(2, 3);
        var records = await Paired.Run(options, plan, FakeBatches(new(), failPiston: "pair-1"), CancellationToken.None);
        Assert.Equal(4, records.Count);
        var failed = Assert.Single(records, row => !row.Correct);
        Assert.Equal("piston", failed.Condition);
        var analysis = Paired.Analyze(options, plan, records, 3, cancelled: false);
        Assert.False((bool)analysis["statistics_eligible"]!);
        Assert.Null(analysis["paired_changes"]);
        Assert.Equal(1, analysis["complete_valid_comparable_pairs"]);
        var pairs = (Dictionary<string, object?>[])analysis["pairs"]!;
        Assert.Contains("piston invalid: MCP failure", (string[])pairs.Single(pair => (string)pair["pair_id"]! == "pair-1")["problems"]!);
        var summary = Evidence.Summarize(records, "paired");
        var groups = (Dictionary<string, object?>[])summary["groups"]!;
        Assert.Equal(2, groups.Single(group => (string)group["condition"]! == "piston")["attempted"]);
        Assert.Equal(1, groups.Single(group => (string)group["condition"]! == "piston")["invalid"]);
    }

    [Fact]
    public void NonComparablePairsWithholdStatistics()
    {
        var plan = Paired.Schedule(2, 5);
        var options = new Options("unit", "regression", "/tmp/x", 2, 2, ApproveCampaign: true, Paired: true);
        Assert.True((bool)Paired.Analyze(options, plan, Rows(plan, 2), 5, false)["statistics_eligible"]!);

        void Withheld(Action<List<Trial>> mutate, string expected, bool cancelled = false, int concurrency = 2)
        {
            var rows = Rows(plan, 2);
            mutate(rows);
            var analysis = Paired.Analyze(options with { Concurrency = concurrency }, plan, rows, 5, cancelled);
            Assert.False((bool)analysis["statistics_eligible"]!);
            Assert.Null(analysis["paired_changes"]);
            var text = string.Join("\n", ((string[])analysis["statistics_withheld_reasons"]!).Concat(
                ((Dictionary<string, object?>[])analysis["pairs"]!).SelectMany(pair => (string[])pair["problems"]!)));
            Assert.Contains(expected, text);
        }
        Withheld(rows => rows[0].EditHash = "other", "edited source hash differs");
        Withheld(rows => rows[0].OracleDiscoveryHash = "other", "baseline test identity hash differs");
        Withheld(rows => rows.First(row => row.Condition == "piston").TestProcessParallelism = 2,
            "test-process parallelism differs");
        Withheld(rows => rows.First(row => row.Condition == "piston").PistonAdmissionIsolated = false,
            "admission was not isolated");
        Withheld(rows => rows.First(row => row.Condition == "piston").OracleKind = "piston-selective",
            "oracle does not match");
        Withheld(rows => rows.RemoveAt(0), "1 of 2 trials attempted");
        Withheld(rows => rows[0].TimedOut = !(rows[0].Correct = false), "invalid: timeout");
        Withheld(_ => { }, "cancelled", cancelled: true);
        Withheld(rows => rows.Add(new Trial
        {
            PairId = "pair-9", BatchId = "pair-9-dotnet", Condition = "dotnet", Workload = "unit", Scenario = "regression"
        }), "outside the recorded plan");
        var single = Paired.Schedule(1, 5);
        var analysis = Paired.Analyze(options, single, Rows(single, 2), 5, false);
        Assert.False((bool)analysis["statistics_eligible"]!);
        Assert.Contains("Fewer than two pairs", string.Join("\n", (string[])analysis["statistics_withheld_reasons"]!));
    }

    [Fact]
    public void PairedChangesUsesBatchMeansAndRejectsSplitOrUnpairedBatches()
    {
        var plan = Paired.Schedule(2, 1);
        var rows = Rows(plan, 2);
        foreach (var row in rows.Where(row => row.Condition == "piston"))
            row.EditToCorrectResultMs = row.TrialId.EndsWith("-0", StringComparison.Ordinal) ? 60 : 80;
        var changes = Evidence.PairedChanges(rows, "piston", 1);
        Assert.Equal(2, changes["paired_batches"]);
        Assert.Equal(.3, (double)changes["median_relative"]!, 10);
        Assert.Equal(30, (double)changes["median_absolute_ms"]!, 10);
        var split = Rows(plan, 2);
        split[0] = Clone(split[0], batchId: "other-batch");
        Assert.Throws<InvalidDataException>(() => Evidence.PairedChanges(split, "piston"));
        var unpaired = Rows(plan, 2);
        unpaired[0] = Clone(unpaired[0], pairId: null);
        Assert.Throws<InvalidDataException>(() => Evidence.PairedChanges(unpaired, "piston"));
    }

    [Theory]
    [InlineData("--repetitions", "2")]
    [InlineData("--concurrency", "2")]
    [InlineData("--workload", "integration")]
    [InlineData("--seed", "-1")]
    [InlineData("--condition", "piston")]
    [InlineData("--condition", "dotnet")]
    public async Task PairedGuardsFailBeforeCreatingArtifacts(string option, string value)
    {
        using var temp = new TemporaryDirectory();
        var output = Path.Combine(temp.Path, "output");
        Assert.Equal(2, await Program.Main(["--output", output, "--paired", "--piston-daemon-dll",
            typeof(Program).Assembly.Location, option, value]));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task PairedRequiresDaemonAndSeedRequiresPaired()
    {
        using var temp = new TemporaryDirectory();
        var output = Path.Combine(temp.Path, "output");
        Assert.Equal(2, await Program.Main(["--output", output, "--paired"]));
        Assert.Equal(2, await Program.Main(["--output", output, "--seed", "1"]));
        Assert.False(Directory.Exists(output));
        var parsed = Options.Parse(["--output", output, "--paired", "--seed", "42"]);
        Assert.True(parsed.Paired);
        Assert.Equal(42, parsed.Seed);
    }

    [Fact]
    public async Task SingleCompilePairRequiresMatchingDiagnosticAndWithholdsStatistics()
    {
        using var temp = new TemporaryDirectory();
        var options = new Options("unit", "compile-error", temp.Path, Timeout: 30,
            PistonDaemonDll: typeof(Program).Assembly.Location, Paired: true);
        options.Validate(FixtureManifest.Load(Path.Combine(Root, "scenarios.json")));
        var plan = Paired.Schedule(1, 42);
        var records = await Paired.Run(options, plan, FakeBatches(new()), CancellationToken.None);
        Assert.Equal(2, records.Count);
        Assert.All(records, row =>
        {
            Assert.True(row.Correct, row.Error);
            Assert.Null(row.ExpectedTests);
            Assert.Null(row.ObservedTests);
            Assert.Equal(new CompileDiagnostic("CS0103", "Pricing/PriceRules.cs", 15), row.ExpectedDiagnostic);
        });
        var analysis = Paired.Analyze(options, plan, records, 42, false);
        Assert.Equal(1, analysis["complete_valid_comparable_pairs"]);
        Assert.False((bool)analysis["statistics_eligible"]!);
        Assert.Null(analysis["paired_changes"]);
        records[0].ExpectedDiagnostic = records[0].ExpectedDiagnostic! with { Line = 16 };
        Assert.Contains("Non-comparable: expected compile diagnostic differs",
            Paired.PairProblems(plan[0], records, 1));
    }

    [Theory]
    [InlineData("--repetitions", "2")]
    [InlineData("--concurrency", "2")]
    [InlineData("--concurrency", "4")]
    public async Task CompilePairCannotBypassCampaignApproval(string option, string value)
    {
        using var temp = new TemporaryDirectory();
        var output = Path.Combine(temp.Path, "output");
        Assert.Equal(2, await Program.Main(["--output", output, "--paired", "--scenario", "compile-error",
            "--piston-daemon-dll", typeof(Program).Assembly.Location, option, value]));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void ExitCodeCoversSingleConditionAndPairedSummaries()
    {
        var plan = Paired.Schedule(2, 5);
        var rows = Rows(plan, 1);
        var options = new Options("unit", "regression", "/tmp/x", 1, 2, ApproveCampaign: true, Paired: true);
        Assert.Equal(0, Program.ExitCode(false, rows, Evidence.Summarize(rows)));
        Assert.Equal(1, Program.ExitCode(true, rows, Evidence.Summarize(rows)));
        var summary = Evidence.Summarize(rows);
        summary["paired"] = Paired.Analyze(options, plan, rows, 5, false);
        Assert.Equal(0, Program.ExitCode(false, rows, summary));
        rows[0].EditHash = "other";
        summary["paired"] = Paired.Analyze(options, plan, rows, 5, false);
        Assert.Equal(1, Program.ExitCode(false, rows, summary));
        rows[0].Correct = false;
        Assert.Equal(1, Program.ExitCode(false, rows, Evidence.Summarize(rows)));
    }

    [Fact]
    public void AdmissionIsolationRequiresSoleAdmittedDefaultBudgetRun()
    {
        JsonObject Admission() => new()
        {
            ["state"] = "Admitted", ["budgetSource"] = "default", ["otherAdmittedRuns"] = 0,
            ["queuedAhead"] = 0, ["reservedCpuSlots"] = 2
        };
        Assert.True(Trials.AdmissionIsolated(Admission(), 2));
        Assert.False(Trials.AdmissionIsolated(null, 2));
        Assert.False(Trials.AdmissionIsolated(Admission(), 1));
        foreach (var (key, value) in new (string, JsonNode)[] { ("state", "Queued"), ("budgetSource", "/budget.json"),
                     ("otherAdmittedRuns", 1), ("queuedAhead", 1) })
        {
            var admission = Admission();
            admission[key] = value;
            Assert.False(Trials.AdmissionIsolated(admission, 2));
        }
    }

    private static List<Trial> Rows(IEnumerable<PairPlan> plan, int concurrency) =>
        plan.SelectMany(pair => pair.Order.SelectMany((condition, position) =>
            Enumerable.Range(0, concurrency).Select(index => new Trial
            {
                TrialId = $"{pair.PairId}-{condition}-{index}",
                PairId = pair.PairId,
                PairPosition = position,
                BatchId = Paired.BatchId(pair, condition),
                Condition = condition,
                Workload = "unit",
                Scenario = "regression",
                Correct = true,
                EditToCorrectResultMs = condition == "dotnet" ? 100 : 70,
                BaseHash = "base", BaselineHash = "baseline", PreEditHash = "pre", EditHash = "edit",
                OracleDiscoveryHash = "discovery",
                ExpectedTests = new() { ["Tests.A"] = "Failed" },
                TestProcessParallelism = 1,
                OracleKind = condition == "dotnet" ? "full-test-scope" : "piston-generation-bound-full-test-scope",
                PistonAdmissionIsolated = condition == "piston" ? true : null
            }))).ToList();

    private static Trial Clone(Trial row, string? batchId = null, string? pairId = "keep") => new()
    {
        TrialId = row.TrialId, PairId = pairId == "keep" ? row.PairId : pairId, PairPosition = row.PairPosition,
        BatchId = batchId ?? row.BatchId, Condition = row.Condition, Workload = row.Workload, Scenario = row.Scenario,
        Correct = row.Correct, EditToCorrectResultMs = row.EditToCorrectResultMs
    };

    private static RunConditionBatch FakeBatches(ConcurrentBag<PistonLaunch> launches, string? failPiston = null)
    {
        var workload = FixtureManifest.Load(Path.Combine(Root, "scenarios.json")).Workloads["unit"];
        var baseline = new Dictionary<string, string>();
        foreach (var name in workload.Scenarios.Values.SelectMany(scenario => scenario.FailedSuffixes)
                     .Concat(workload.Scenarios.Values.SelectMany(scenario => scenario.BaselineFailedSuffixes)).Distinct())
            baseline["Tests." + name] = "Passed";
        for (var index = baseline.Count; index < workload.MinimumTests; index++)
            baseline["Tests.Case" + index] = "Passed";
        return async (options, batchId, pairId, position, token) =>
        {
            var scenario = workload.Scenarios[options.Scenario];
            var expected = Evidence.ExpectedResults(baseline, scenario.FailedSuffixes);
            Task<CommandResult> Execute(string[] argv, string cwd, string artifacts, string label,
                double timeout, Dictionary<string, string>? environment, CancellationToken cancellationToken)
            {
                var tests = label == "edited" ? expected : baseline;
                if (label == "edited" && scenario.Diagnostic is not null)
                {
                    File.WriteAllText(Path.Combine(artifacts, "edited.log"),
                        $"{Path.Combine(cwd, workload.Source)}(15,9): error CS0103: MissingSymbol");
                    return Task.FromResult(new CommandResult(argv, 1, 1, false, 1, 0, label + ".log", 0, 0));
                }
                if (label != "restore")
                    HarnessTests.WriteTrx(argv[Array.IndexOf(argv, "--results-directory") + 1], tests);
                return Task.FromResult(new CommandResult(argv, 1,
                    label != "restore" && tests.Values.Contains("Failed") ? 1 : 0, false, 1, 0, label + ".log", 0, 0));
            }
            var barrier = new EditBarrier(options.Concurrency);
            return await Task.WhenAll(Enumerable.Range(0, options.Concurrency).Select(index =>
                Trials.Run(Root, options, workload, batchId, index, barrier, token, Execute,
                    launch =>
                    {
                        Assert.False(Directory.Exists(launch.ExecutionDirectory));
                        launches.Add(launch);
                        return new PistonTrialTests.FakePiston(launch.Solution,
                            Path.Combine(launch.Workspace, workload.Source), expected, baseline, null, false, false,
                            fail: pairId == failPiston, false, launch.Parallelism, delayMs: 5,
                            diagnostic: scenario.Diagnostic);
                    }, pairId, position)));
        };
    }
}
