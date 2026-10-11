// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Security.Cryptography;

namespace Piston.Measurements;

/// <summary>One counterbalanced pair: the same workload/scenario/repetition/concurrency under both conditions.</summary>
public sealed record PairPlan(string PairId, int Repetition, string[] Order);

public delegate Task<Trial[]> RunConditionBatch(Options options, string batchId, string pairId, int position,
    CancellationToken cancellationToken);

public static class Paired
{
    public static readonly string[] Conditions = ["dotnet", "piston"];

    public static int NewSeed() => RandomNumberGenerator.GetInt32(int.MaxValue);

    /// <summary>
    /// Balanced counterbalancing: half the pairs run dotnet first, half piston first (an odd pair is
    /// assigned by the seed), shuffled with the recorded seed. The plan is recorded before any trial runs.
    /// </summary>
    public static PairPlan[] Schedule(int pairs, int seed)
    {
        if (pairs < 1) throw new ArgumentException("At least one pair is required");
        var random = new Random(seed);
        var dotnetFirst = Enumerable.Range(0, pairs).Select(index => index < pairs / 2).ToArray();
        if (pairs % 2 == 1) dotnetFirst[^1] = random.Next(2) == 0;
        for (var index = pairs - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (dotnetFirst[index], dotnetFirst[swap]) = (dotnetFirst[swap], dotnetFirst[index]);
        }
        return dotnetFirst.Select((first, repetition) => new PairPlan(
            "pair-" + repetition.ToString(CultureInfo.InvariantCulture), repetition,
            first ? ["dotnet", "piston"] : ["piston", "dotnet"])).ToArray();
    }

    public static string BatchId(PairPlan plan, string condition) => $"{plan.PairId}-{condition}";

    /// <summary>Runs every planned batch sequentially; conditions never overlap, every attempt is retained.</summary>
    public static async Task<List<Trial>> Run(Options options, IReadOnlyList<PairPlan> plan,
        RunConditionBatch runBatch, CancellationToken cancellationToken)
    {
        var records = new List<Trial>();
        foreach (var pair in plan)
            for (var position = 0; position < pair.Order.Length; position++)
            {
                if (cancellationToken.IsCancellationRequested) return records;
                var condition = pair.Order[position];
                records.AddRange(await runBatch(options with { Condition = condition },
                    BatchId(pair, condition), pair.PairId, position, cancellationToken));
            }
        return records;
    }

    public static RunConditionBatch TrialBatches(string root, Workload workload) =>
        async (options, batchId, pairId, position, cancellationToken) =>
        {
            var barrier = new EditBarrier(options.Concurrency);
            return await Task.WhenAll(Enumerable.Range(0, options.Concurrency).Select(index =>
                Trials.Run(root, options, workload, batchId, index, barrier, cancellationToken,
                    pairId: pairId, pairPosition: position)));
        };

    /// <summary>Reasons a pair cannot contribute to paired statistics; empty when valid and comparable.</summary>
    public static List<string> PairProblems(PairPlan plan, IReadOnlyCollection<Trial> rows, int concurrency)
    {
        var problems = new List<string>();
        foreach (var condition in Conditions)
        {
            var batch = rows.Where(row => row.Condition == condition).ToArray();
            if (batch.Length != concurrency)
                problems.Add($"{condition}: {batch.Length} of {concurrency} trials attempted");
            if (batch.Any(row => row.BatchId != BatchId(plan, condition)))
                problems.Add($"{condition}: unexpected batch identity");
            foreach (var row in batch.Where(row => !row.Correct))
                problems.Add($"{condition} invalid: {(row.TimedOut ? "timeout: " : "")}{row.Error ?? "no valid latency"}");
            if (batch.Any(row => row.PairPosition != Array.IndexOf(plan.Order, condition)))
                problems.Add($"{condition}: order differs from the recorded plan");
        }
        if (rows.Any(row => row.PairId != plan.PairId))
            problems.Add("Trial pair identity differs from the plan");
        if (problems.Count > 0) return problems;
        void Same<T>(string label, Func<Trial, T> value)
        {
            if (rows.Select(value).Distinct().Count() != 1) problems.Add($"Non-comparable: {label} differs");
        }
        Same("workload/scenario", row => (row.Workload, row.Scenario));
        Same("base source hash", row => row.BaseHash);
        Same("baseline source hash", row => row.BaselineHash);
        Same("pre-edit source hash", row => row.PreEditHash);
        Same("edited source hash", row => row.EditHash);
        Same("baseline test identity hash", row => row.OracleDiscoveryHash);
        Same("expected test identities/statuses", row => string.Join("\n",
            (row.ExpectedTests ?? []).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "\t" + pair.Value)));
        Same("test-process parallelism", row => row.TestProcessParallelism);
        Same("expected compile diagnostic", row => row.ExpectedDiagnostic);
        var compile = rows.Any(row => row.ExpectedDiagnostic is not null);
        if (rows.Any(row => row.Condition == "dotnet" &&
                row.OracleKind != (compile ? "compile-diagnostic" : "full-test-scope")) ||
            rows.Any(row => row.Condition == "piston" &&
                row.OracleKind != (compile ? "piston-generation-bound-compile-diagnostic" : "piston-generation-bound-full-test-scope")))
            problems.Add("Non-comparable: oracle does not match full test scope or compile diagnostic");
        if (rows.Any(row => row.Condition == "piston" && row.PistonAdmissionIsolated != true))
            problems.Add("Non-comparable: Piston admission was not isolated (shared budget, queueing or other admitted runs)");
        return problems;
    }

    public static Dictionary<string, object?> Analyze(Options options, IReadOnlyList<PairPlan> plan,
        IReadOnlyCollection<Trial> records, int seed, bool cancelled)
    {
        var pairs = plan.Select(pair =>
        {
            var rows = records.Where(row => row.PairId == pair.PairId).ToArray();
            var problems = PairProblems(pair, rows, options.Concurrency);
            return (Pair: pair, Rows: rows, Problems: problems);
        }).ToArray();
        var reasons = new List<string>();
        if (cancelled) reasons.Add("Campaign cancelled before every planned pair completed");
        if (records.Any(row => row.PairId is null || plan.All(pair => pair.PairId != row.PairId)))
            reasons.Add("Trials outside the recorded plan");
        var unusable = pairs.Count(pair => pair.Problems.Count > 0);
        if (unusable > 0)
            reasons.Add($"{unusable} of {plan.Count} pairs invalid or non-comparable; no pair is discarded to rescue statistics");
        if (plan.Count < 2) reasons.Add("Fewer than two pairs: no batch-level uncertainty is possible");
        Dictionary<string, object?>? statistics = null;
        if (reasons.Count == 0)
            statistics = Evidence.PairedChanges(records, "piston", seed);
        return new()
        {
            ["design"] = "counterbalanced sequential condition batches; batch is the unit of analysis",
            ["seed"] = seed,
            ["planned_pairs"] = plan.Count,
            ["complete_valid_comparable_pairs"] = pairs.Count(pair => pair.Problems.Count == 0),
            ["pairs"] = pairs.Select(pair => new Dictionary<string, object?>
            {
                ["pair_id"] = pair.Pair.PairId,
                ["repetition"] = pair.Pair.Repetition,
                ["order"] = pair.Pair.Order,
                ["batches"] = Conditions.ToDictionary(condition => condition, condition => new Dictionary<string, object?>
                {
                    ["batch_id"] = BatchId(pair.Pair, condition),
                    ["trial_ids"] = pair.Rows.Where(row => row.Condition == condition).Select(row => row.TrialId).ToArray(),
                    ["valid"] = pair.Rows.Count(row => row.Condition == condition && row.Correct),
                    ["timeouts"] = pair.Rows.Count(row => row.Condition == condition && row.TimedOut)
                }),
                ["usable"] = pair.Problems.Count == 0,
                ["problems"] = pair.Problems.ToArray()
            }).ToArray(),
            ["statistics_eligible"] = statistics is not null,
            ["statistics_withheld_reasons"] = reasons.ToArray(),
            ["paired_changes"] = statistics
        };
    }
}
