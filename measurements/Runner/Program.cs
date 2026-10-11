// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Runtime.InteropServices;
using System.Text.Json;

namespace Piston.Measurements;

public static class Program
{
    public const string Usage = """
        .NET measurement runner (Linux only; no implicit agents or containers).
        --output PATH [--workload unit|integration] [--scenario NAME]
        [--condition dotnet|piston | --paired [--seed N]] [--piston-daemon-dll PATH]
        [--concurrency 1|2|4] [--repetitions N] [--timeout SECONDS]
        [--approve-campaign] [--approve-containers]
        Integration requires an approved immutable MEASUREMENT_POSTGRES_IMAGE
        and local unix:// DOCKER_HOST. Build the runner before measuring.
        Piston requires a prebuilt daemon DLL; unit per-test and compile-diagnostic scenarios are supported.
        --paired runs counterbalanced dotnet/piston batch pairs (one pair per repetition);
        it is blocked for integration scenarios.
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Measurement runner requires Linux /proc");
            var root = FindRoot();
            var options = Options.Parse(args);
            var manifest = FixtureManifest.Load(Path.Combine(root, "scenarios.json"));
            var workload = options.Validate(manifest);
            var seed = options.Paired ? options.Seed ?? Paired.NewSeed() : (int?)null;
            var plan = seed is { } value ? Paired.Schedule(options.Repetitions, value) : null;
            CreateOutputDirectory(options.Output);
            var context = Evidence.HostContext();
            context["schema_version"] = 1;
            context["experiment_id"] = Path.GetFileName(options.Output);
            context["source"] = "scripted";
            context["runner"] = ".NET";
            context["runner_runtime"] = RuntimeInformation.FrameworkDescription;
            context["runner_assembly_sha256"] = Evidence.Digest(File.ReadAllBytes(typeof(Program).Assembly.Location));
            context["runner_build_policy"] = "prebuilt assembly; no runner build during trials";
            context["workload"] = options.Workload;
            context["scenario"] = options.Scenario;
            context["conditions"] = options.Paired ? Paired.Conditions : new[] { options.Condition };
            context["mode"] = options.Paired ? "paired" : "single-condition";
            context["paired_design"] = plan is null ? null : new Dictionary<string, object?>
            {
                ["seed"] = seed,
                ["seed_source"] = options.Seed is null ? "generated" : "operator",
                ["counterbalancing"] = "balanced dotnet-first/piston-first counts, shuffled with the seed",
                ["pairing_keys"] = new[] { "workload", "scenario", "repetition", "concurrency" },
                ["batch_execution"] = "condition batches run sequentially, never overlapping; each batch runs " +
                    "--concurrency independent workspaces behind one edit barrier",
                ["unit_of_analysis"] = "pair of condition batch means; concurrent trials are not independent samples",
                ["statistics_policy"] = "computed only when every planned pair is complete, correct and comparable " +
                    "and at least two pairs exist; no pair is dropped",
                ["plan"] = plan.Select(pair => new { pair_id = pair.PairId, pair.Repetition, order = pair.Order,
                    batch_ids = pair.Order.Select(condition => Paired.BatchId(pair, condition)).ToArray() }).ToArray()
            };
            context["piston_daemon_dll"] = options.PistonDaemonDll;
            context["piston_daemon_assembly_sha256"] = options.PistonDaemonDll is null ? null :
                Evidence.Digest(File.ReadAllBytes(options.PistonDaemonDll));
            context["piston_policy"] = options.Condition == "piston" || options.Paired ?
                "isolated baseline-warmed daemon; watching enabled; watcher settling and forced full MCP run included in edit latency; " +
                (options.Paired ? "test-process parallelism matched to dotnet's nominal test-project concurrency"
                    : "test-process parallelism 1") : null;
            context["dotnet_policy"] = "dotnet test full solution, --no-restore, incremental build, trx logger; " +
                "default MSBuild node count and test-project concurrency";
            context["piston_admission_policy"] = options.Condition == "piston" || options.Paired ?
                $"per-trial {PistonLaunch.ExecutionDirectoryVariable} under the trial artifacts with no budget.json " +
                "(engine default budget: one CPU slot per logical CPU); isolated from the operator's daemons and " +
                "other trials, so Piston cross-daemon admission/scheduling is not exercised" : null;
            context["concurrency"] = options.Concurrency;
            context["repetitions"] = options.Repetitions;
            context["timeout_seconds"] = options.Timeout;
            context["cache_policy"] = "restored and baseline-warmed";
            context["cache_limitations"] = "Fresh fixture copy per trial, locked restore from the shared user NuGet cache, " +
                "plain-dotnet full baseline warms build outputs; Piston additionally completes a validated daemon " +
                "startup run. MSBuild node reuse, compiler server, OS page cache and NuGet caches are shared across " +
                "trials and not reset: matched cache state is not enforced.";
            context["fixture_hash"] = Evidence.SourceHash(Path.Combine(root, "fixtures"));
            context["test_scope"] = "full solution";
            context["repository_revision"] = await Processes.Capture(["git", "rev-parse", "HEAD"], root);
            context["scenario_definition"] = workload.Scenarios[options.Scenario];
            context["sdk"] = await Processes.Capture(["dotnet", "--version"], Path.Combine(root, "fixtures"));
            context["os"] = RuntimeInformation.OSDescription;
            context["cpu_count"] = Environment.ProcessorCount;
            context["image"] = Environment.GetEnvironmentVariable("MEASUREMENT_POSTGRES_IMAGE");
            context["telemetry_cadence_ms"] = 50;
            context["permissions"] = new { campaign = options.ApproveCampaign, containers = options.ApproveContainers };
            context["comparator"] = new
            {
                status = options.Paired ? "unit-full-scope-paired" : "unit-full-scope-supported",
                reason = "Piston trials use generation-bound full-scope test or compile-diagnostic oracles; paired mode is unit only. " +
                    "Compile diagnostics use run-local build errors and source provenance. " +
                    "PostgreSQL, larger repositories and telemetry-overhead quantification remain blocked"
            };
            Evidence.WriteJson(Path.Combine(options.Output, "manifest.json"), context);
            var records = new List<Trial>();
            Dictionary<string, object?> summary;
            if (plan is not null)
            {
                records = await Paired.Run(options, plan, Paired.TrialBatches(root, workload), cancel.Token);
                var analysis = Paired.Analyze(options, plan, records, seed!.Value, cancel.IsCancellationRequested);
                summary = Evidence.Summarize(records,
                    "Scripted paired comparison; not a real-agent result and not a G1 decision");
                summary["mode"] = "paired";
                summary["G1"] = "not evaluated";
                summary["paired"] = analysis;
                summary["paired_improvement"] = analysis["paired_changes"];
                summary["uncertainty"] = analysis["statistics_eligible"] is true
                    && analysis["paired_changes"] is Dictionary<string, object?> changes
                    ? changes.GetValueOrDefault("bootstrap_95_percent_interval")
                    : null;
            }
            else
            {
                for (var repetition = 0; repetition < options.Repetitions && !cancel.IsCancellationRequested; repetition++)
                {
                    var barrier = new EditBarrier(options.Concurrency);
                    records.AddRange(await Task.WhenAll(Enumerable.Range(0, options.Concurrency).Select(index =>
                        Trials.Run(root, options, workload, $"batch-{repetition}", index, barrier, cancel.Token))));
                }
                summary = Evidence.Summarize(records);
            }
            Evidence.WriteLines(Path.Combine(options.Output, "trials.jsonl"), records);
            Evidence.WriteJson(Path.Combine(options.Output, "summary.json"), summary);
            Console.WriteLine(JsonSerializer.Serialize(summary, Evidence.Json));
            return ExitCode(cancel.IsCancellationRequested, records, summary);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException or JsonException or
            FormatException or OverflowException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("Measurement runner: " + error.Message);
            return 2;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    /// <summary>Success requires every attempt correct and, in paired mode, every planned pair usable.</summary>
    public static int ExitCode(bool cancelled, IEnumerable<Trial> records, IReadOnlyDictionary<string, object?> summary)
    {
        var comparable = summary.GetValueOrDefault("paired") is not Dictionary<string, object?> paired ||
            (int)paired["complete_valid_comparable_pairs"]! == (int)paired["planned_pairs"]!;
        return !cancelled && records.All(record => record.Correct) && comparable ? 0 : 1;
    }

    public static void CreateOutputDirectory(string output)
    {
        // Atomic private directory creation refuses existing artifacts, including empty directories.
        var parent = Path.GetDirectoryName(output);
        if (parent is not null) Directory.CreateDirectory(parent);
        if (NativeMethods.MakeDirectory(output, Convert.ToUInt32("700", 8)) != 0)
            throw new IOException("Cannot create a new output directory: " + output,
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
    }

    public static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "scenarios.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "fixtures")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate measurements/scenarios.json alongside the runner");
    }
}

internal static class NativeMethods
{
    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    internal static extern int MakeDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
