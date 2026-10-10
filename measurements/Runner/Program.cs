using System.Runtime.InteropServices;
using System.Text.Json;

namespace Piston.Measurements;

public static class Program
{
    public const string Usage = """
        .NET baseline measurement runner (Linux only; no implicit agents or containers).
        --output PATH [--workload unit|integration] [--scenario NAME]
        [--concurrency 1|2|4] [--repetitions N] [--timeout SECONDS]
        [--approve-campaign] [--approve-containers]
        Integration requires an approved immutable MEASUREMENT_POSTGRES_IMAGE
        and local unix:// DOCKER_HOST. Build the runner before measuring.
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
            // Atomic directory creation refuses existing artifacts, including empty directories.
            Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
            if (NativeMethods.MakeDirectory(options.Output, Convert.ToUInt32("755", 8)) != 0)
                throw new IOException("Cannot create a new output directory: " + options.Output,
                    new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
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
            context["conditions"] = new[] { "dotnet" };
            context["concurrency"] = options.Concurrency;
            context["repetitions"] = options.Repetitions;
            context["timeout_seconds"] = options.Timeout;
            context["cache_policy"] = "restored and baseline-warmed";
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
                status = "blocked",
                reason = "Current MCP lacks generation-bound full-scope result evidence"
            };
            Evidence.WriteJson(Path.Combine(options.Output, "manifest.json"), context);
            var records = new List<Trial>();
            for (var repetition = 0; repetition < options.Repetitions && !cancel.IsCancellationRequested; repetition++)
            {
                var barrier = new EditBarrier(options.Concurrency);
                records.AddRange(await Task.WhenAll(Enumerable.Range(0, options.Concurrency).Select(index =>
                    Trials.Run(root, options, workload, $"batch-{repetition}", index, barrier, cancel.Token))));
            }
            Evidence.WriteLines(Path.Combine(options.Output, "trials.jsonl"), records);
            var summary = Evidence.Summarize(records);
            Evidence.WriteJson(Path.Combine(options.Output, "summary.json"), summary);
            Console.WriteLine(JsonSerializer.Serialize(summary, Evidence.Json));
            return !cancel.IsCancellationRequested && records.All(record => record.Correct) ? 0 : 1;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException or JsonException or
            FormatException or OverflowException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("Measurement runner: " + error.Message);
            return 2;
        }
        finally { Console.CancelKeyPress -= handler; }
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
