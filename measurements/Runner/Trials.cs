using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace Piston.Measurements;

public sealed class Trial
{
    public int SchemaVersion { get; init; } = 1;
    public string ExperimentId { get; init; } = "";
    public string TrialId { get; init; } = "";
    public string BatchId { get; init; } = "";
    public string Condition { get; init; } = "dotnet";
    public string Workload { get; init; } = "";
    public string Scenario { get; init; } = "";
    public bool Correct { get; set; }
    public bool TimedOut { get; set; }
    public double? EditToCorrectResultMs { get; set; }
    public double? AgentBlockedWaitMs { get; init; }
    public int? PreExistingFailureTurns { get; init; }
    public int? TotalTurns { get; init; }
    public string AgentMetricReason { get; init; } = "Scripted commands are not agent sessions";
    public List<Dictionary<string, object?>> Incidents { get; } = [];
    public Dictionary<string, double>? StageDurationsMs { get; init; }
    public string StageDurationReason { get; init; } = "Command-level timing only; no build/test stage instrumentation";
    public ContainerStats? ContainerStats { get; set; }
    public string? ContainerStatsReason { get; set; } = "Unit workload has no containers";
    public object? ContainerCleanup { get; set; }
    public string ArtifactDirectory { get; init; } = "";
    public string? Error { get; set; }
    public string? BaseHash { get; set; }
    public string? BaselineHash { get; set; }
    public string? PreEditHash { get; set; }
    public string? EditHash { get; set; }
    public string? OracleDiscoveryHash { get; set; }
    public string? OracleKind { get; set; }
    public CommandResult? Restore { get; set; }
    public CommandResult? BaselineSetup { get; set; }
    public CommandResult? RepairPreparation { get; set; }
    public CommandResult? Execution { get; set; }
    public Dictionary<string, string>? BaselineTests { get; set; }
    public Dictionary<string, string>? ExpectedTests { get; set; }
    public Dictionary<string, string>? ObservedTests { get; set; }
}

public sealed class EditBarrier(int participants)
{
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int remaining = participants;
    public async Task Wait(double timeout, CancellationToken cancellationToken)
    {
        if (Interlocked.Decrement(ref remaining) == 0) completion.TrySetResult();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(timeout), cancellationToken);
    }
    public void Abort() => completion.TrySetException(new InvalidDataException("Shared edit barrier aborted"));
}

public delegate Task<CommandResult> ExecuteCommand(string[] argv, string cwd, string artifacts,
    string label, double timeout, Dictionary<string, string>? environment, CancellationToken cancellationToken);

public static class Trials
{
    public static async Task<Trial> Run(string root, Options options, Workload workload, string batchId,
        int index, EditBarrier barrier, CancellationToken cancellationToken = default,
        ExecuteCommand? execute = null)
    {
        execute ??= Processes.Command;
        var scenario = workload.Scenarios[options.Scenario];
        var experiment = Path.GetFileName(options.Output);
        var trialId = $"{experiment}-{batchId}-{index}-{Guid.NewGuid():N}";
        var artifacts = Path.Combine(options.Output, trialId);
        Directory.CreateDirectory(artifacts);
        var workspace = Path.Combine(artifacts, "workspace");
        var events = new List<Dictionary<string, object?>>();
        var origin = Evidence.NowNs;
        void Event(string type, Dictionary<string, object?>? details = null)
        {
            var value = new Dictionary<string, object?>
            {
                ["schema_version"] = 1,
                ["experiment_id"] = experiment,
                ["trial_id"] = trialId,
                ["batch_id"] = batchId,
                ["session_id"] = null,
                ["source"] = "scripted",
                ["type"] = type,
                ["utc"] = DateTime.UtcNow.ToString("O"),
                ["elapsed_ns"] = Evidence.NowNs - origin
            };
            if (details is not null)
                foreach (var pair in details) value[pair.Key] = pair.Value;
            events.Add(value);
        }
        var record = new Trial
        {
            ExperimentId = experiment,
            TrialId = trialId,
            BatchId = batchId,
            Workload = options.Workload,
            Scenario = options.Scenario,
            ArtifactDirectory = artifacts
        };
        ContainerApi? api = null;
        ContainerObserver? observer = null;
        try
        {
            Evidence.CopyFixture(Path.Combine(root, "fixtures"), workspace);
            var solution = Path.Combine(workspace, workload.Solution);
            var source = Path.Combine(workspace, workload.Source);
            var environment = new Dictionary<string, string>
            {
                ["MEASUREMENT_TRIAL_ID"] = trialId,
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
            };
            if (options.Workload == "integration")
            {
                api = new ContainerApi();
                observer = new ContainerObserver(api, trialId);
                observer.Start();
            }
            record.BaseHash = Evidence.SourceHash(workspace);
            if (scenario.SeedBefore is not null)
                File.WriteAllText(source, ReplaceUnique(File.ReadAllText(source), scenario.SeedBefore, scenario.SeedAfter!));
            record.BaselineHash = Evidence.SourceHash(workspace);
            record.Restore = await execute(
                ["dotnet", "restore", solution, "--locked-mode", "--verbosity", "quiet"],
                workspace, artifacts, "restore", options.Timeout, environment, cancellationToken);
            record.TimedOut = record.Restore.TimedOut;
            if (record.TimedOut || record.Restore.ExitCode != 0)
                throw new InvalidDataException("Restore failed; see restore.log");
            var baselineDirectory = Path.Combine(artifacts, "baseline");
            Directory.CreateDirectory(baselineDirectory);
            if (observer is not null) observer.Phase = "baseline";
            record.BaselineSetup = await execute(Processes.TestCommand(solution, baselineDirectory),
                workspace, artifacts, "baseline", options.Timeout, environment, cancellationToken);
            record.TimedOut = record.BaselineSetup.TimedOut;
            if (record.TimedOut) throw new InvalidDataException("Baseline timed out");
            var baseline = Evidence.ParseTrx(baselineDirectory, record.BaselineSetup.StartedWallNs);
            if (baseline.Count < workload.MinimumTests) throw new InvalidDataException("Baseline scope is incomplete");
            var clean = baseline.ToDictionary(pair => pair.Key, _ => "Passed", StringComparer.Ordinal);
            Evidence.Oracle(Evidence.ExpectedResults(clean, scenario.BaselineFailedSuffixes),
                baseline, record.BaselineSetup.ExitCode);
            record.BaselineTests = baseline;
            var original = File.ReadAllText(source);
            var edited = ReplaceUnique(original, scenario.Before, scenario.After);
            if (scenario.Repair)
            {
                File.WriteAllText(source, edited);
                var preparation = Path.Combine(artifacts, "repair-preparation");
                Directory.CreateDirectory(preparation);
                if (observer is not null) observer.Phase = "repair-preparation";
                record.RepairPreparation = await execute(Processes.TestCommand(solution, preparation),
                    workspace, artifacts, "repair-preparation", options.Timeout, environment, cancellationToken);
                record.TimedOut = record.RepairPreparation.TimedOut;
                if (record.TimedOut) throw new InvalidDataException("Repair preparation timed out");
                Evidence.Oracle(Evidence.ExpectedResults(baseline, workload.Scenarios["regression"].FailedSuffixes),
                    Evidence.ParseTrx(preparation, record.RepairPreparation.StartedWallNs),
                    record.RepairPreparation.ExitCode);
                edited = original;
            }
            record.PreEditHash = Evidence.SourceHash(workspace);
            // Match the original runner's sorted discovery-list hash representation.
            record.OracleDiscoveryHash = Evidence.Digest("[" +
                string.Join(", ", baseline.Keys.Order(StringComparer.Ordinal)
                    .Select(name => JsonSerializer.Serialize(name))) + "]");
            var expected = Evidence.ExpectedResults(baseline, scenario.FailedSuffixes);
            var previousSource = File.ReadAllText(source);
            Event("queued");
            await barrier.Wait(options.Timeout, cancellationToken);
            File.WriteAllText(source, edited);
            var editTime = Evidence.NowNs;
            Event("edit-applied", new() { ["patch_hash"] = Evidence.Digest(previousSource + "\0" + edited) });
            record.EditHash = Evidence.SourceHash(workspace);
            var resultsDirectory = Path.Combine(artifacts, "edited");
            Directory.CreateDirectory(resultsDirectory);
            Event("command-started");
            if (observer is not null) observer.Phase = "edited";
            record.Execution = await execute(Processes.TestCommand(solution, resultsDirectory),
                workspace, artifacts, "edited", options.Timeout, environment, cancellationToken);
            var available = Evidence.NowNs;
            Event("result-available", new() { ["exit_code"] = record.Execution.ExitCode });
            record.TimedOut = record.Execution.TimedOut;
            if (record.TimedOut) throw new InvalidDataException("Measured command timed out");
            if (Evidence.SourceHash(workspace) != record.EditHash)
                throw new InvalidDataException("Source changed during measured execution");
            if (scenario.Diagnostic is not null)
            {
                var text = File.ReadAllText(Path.Combine(artifacts, "edited.log"));
                if (record.Execution.ExitCode == 0 ||
                    !Regex.IsMatch(text, @"\berror " + Regex.Escape(scenario.Diagnostic) + @"\b"))
                    throw new InvalidDataException("Expected compile diagnostics absent");
                if (Directory.GetFiles(resultsDirectory, "*.trx").Length > 0)
                    throw new InvalidDataException("Compile-error trial unexpectedly produced test results");
                record.OracleKind = "compile-diagnostic";
            }
            else
            {
                var observed = Evidence.ParseTrx(resultsDirectory, record.Execution.StartedWallNs);
                Evidence.Oracle(expected, observed, record.Execution.ExitCode);
                record.ExpectedTests = expected;
                record.ObservedTests = observed;
                record.OracleKind = "full-test-scope";
            }
            record.Correct = true;
            record.EditToCorrectResultMs = (available - editTime) / 1e6;
            Event("oracle-checked", new() { ["correct"] = true });
        }
        catch (Exception error) when (error is InvalidDataException or IOException or XmlException or KeyNotFoundException or
            ArgumentException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            barrier.Abort();
            record.Error = error.Message;
            if (record.TimedOut)
            {
                record.Incidents.Add(new()
                {
                    ["kind"] = "timeout",
                    ["attribution"] = "unclassified",
                    ["evidence"] = error.Message
                });
                Event("timeout", new() { ["error"] = error.Message });
            }
            Event("incident", new() { ["error"] = error.Message });
        }
        finally
        {
            if (observer is not null)
            {
                try
                {
                    await observer.Stop();
                    record.ContainerStats = observer.Stats;
                    record.ContainerStatsReason = observer.Stats.Errors.Count > 0 ||
                        !observer.Stats.Samples.Any(sample => sample.Phase == "edited")
                        ? "Telemetry errors or no edited-command samples" : null;
                    var cleanup = await observer.Finish();
                    record.ContainerCleanup = cleanup;
                    if (cleanup.RemovedByHarness.Length > 0)
                        record.Incidents.Add(new()
                        {
                            ["kind"] = "container-cleanup-required",
                            ["attribution"] = "infrastructure",
                            ["evidence"] = cleanup.RemovedByHarness
                        });
                    Event("container-cleanup", new()
                    {
                        ["confirmed"] = cleanup.Confirmed,
                        ["remaining"] = cleanup.Remaining,
                        ["removed_by_harness"] = cleanup.RemovedByHarness,
                        ["observed_container_ids"] = cleanup.ObservedContainerIds,
                        ["scope"] = cleanup.Scope
                    });
                }
                catch (Exception error) when (ContainerObserver.IsTelemetryError(error))
                {
                    record.Correct = false;
                    record.EditToCorrectResultMs = null;
                    record.Error = "Container cleanup failed: " + error.Message;
                    record.ContainerCleanup = new { confirmed = false, error = error.Message };
                    record.Incidents.Add(new()
                    {
                        ["kind"] = "container-cleanup-failure",
                        ["attribution"] = "infrastructure",
                        ["evidence"] = error.Message
                    });
                }
                observer.Dispose();
                api!.Dispose();
            }
            Event("cleanup", new() { ["status"] = "workspace retained; subprocesses completed or terminated" });
            Evidence.WriteJson(Path.Combine(artifacts, "trial.json"), record);
            Evidence.WriteLines(Path.Combine(artifacts, "events.jsonl"), events);
        }
        return record;
    }

    public static string ReplaceUnique(string source, string before, string after)
    {
        var index = source.IndexOf(before, StringComparison.Ordinal);
        if (before.Length == 0 || index < 0 || source.IndexOf(before, index + before.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("Edit anchor is not unique");
        return source[..index] + after + source[(index + before.Length)..];
    }
}
