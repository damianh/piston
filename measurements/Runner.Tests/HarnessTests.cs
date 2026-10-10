using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Measurements;
using Xunit;

namespace Piston.Measurements.Tests;

public sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "piston-measurement-test-" + Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public sealed class HarnessTests
{
    private static string Root => Program.FindRoot();

    [Fact]
    public void ManifestRejectsEscapingPathsAndInvalidEdits()
    {
        Assert.Equal(1, FixtureManifest.Load(System.IO.Path.Combine(Root, "scenarios.json")).SchemaVersion);
        using var temp = new TemporaryDirectory();
        var node = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(Root, "scenarios.json")))!;
        node["workloads"]!["unit"]!["source"] = "../escape.cs";
        var path = System.IO.Path.Combine(temp.Path, "manifest.json");
        File.WriteAllText(path, node.ToJsonString());
        Assert.Throws<InvalidDataException>(() => FixtureManifest.Load(path));
        node["workloads"]!["unit"]!["source"] = "unit/Price.cs";
        node["workloads"]!["unit"]!["scenarios"]!["baseline"]!["after"] = "DiscountThreshold = 100m";
        File.WriteAllText(path, node.ToJsonString());
        Assert.Throws<InvalidDataException>(() => FixtureManifest.Load(path));
    }

    [Fact]
    public void OracleRejectsScopeStatusAndExitMismatch()
    {
        var expected = new Dictionary<string, string> { ["Tests.Boundary"] = "Passed", ["Tests.Existing"] = "Failed" };
        Evidence.Oracle(expected, new Dictionary<string, string>(expected), 1);
        Assert.Throws<InvalidDataException>(() => Evidence.Oracle(expected, new Dictionary<string, string>(), 1));
        Assert.Throws<InvalidDataException>(() => Evidence.Oracle(expected, new Dictionary<string, string>(expected), 0));
        Assert.Throws<InvalidDataException>(() => Evidence.Oracle(expected,
            new Dictionary<string, string> { ["Tests.Boundary"] = "Failed", ["Tests.Existing"] = "Failed" }, 1));
    }

    [Fact]
    public void TrxRequiresFreshUniqueCompleteIdentities()
    {
        using var temp = new TemporaryDirectory();
        Assert.Throws<InvalidDataException>(() => Evidence.ParseTrx(temp.Path));
        WriteTrx(temp.Path, new() { ["Tests.One"] = "Passed", ["Tests.Skipped"] = "NotExecuted" });
        Assert.Equal(2, Evidence.ParseTrx(temp.Path).Count);
        Assert.Throws<InvalidDataException>(() => Evidence.ParseTrx(temp.Path, long.MaxValue));
        var path = System.IO.Path.Combine(temp.Path, "test.trx");
        var text = File.ReadAllText(path);
        File.WriteAllText(path, text.Replace("total=\"2\"", "total=\"3\"", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => Evidence.ParseTrx(temp.Path));
        File.WriteAllText(path, text.Replace("Tests.Skipped", "Tests.One", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => Evidence.ParseTrx(temp.Path));
        File.WriteAllText(path, text.Replace("NotExecuted", "Unknown", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => Evidence.ParseTrx(temp.Path));
    }

    [Fact]
    public void SentinelsAndEditsMustBeUnique()
    {
        var baseline = new Dictionary<string, string> { ["Tests.Price.Boundary"] = "Passed" };
        Assert.Equal("Failed", Evidence.ExpectedResults(baseline, ["Price.Boundary"])["Tests.Price.Boundary"]);
        Assert.Throws<InvalidDataException>(() => Evidence.ExpectedResults(baseline, ["Missing"]));
        baseline["Other.Price.Boundary"] = "Passed";
        Assert.Throws<InvalidDataException>(() => Evidence.ExpectedResults(baseline, ["Price.Boundary"]));
        Assert.Equal("a changed b", Trials.ReplaceUnique("a old b", "old", "changed"));
        Assert.Throws<InvalidDataException>(() => Trials.ReplaceUnique("old old", "old", "changed"));
        Assert.Throws<InvalidDataException>(() => Trials.ReplaceUnique("nothing", "old", "changed"));
    }

    [Fact]
    public void SourceHashAndCopiesExcludeBuildArtifacts()
    {
        Assert.Equal("585a57913afde1805a8a39b437091b9b2961adc1fc0d6b964ecc1fa5f518cc05",
            Evidence.SourceHash(System.IO.Path.Combine(Root, "fixtures")));
        using var temp = new TemporaryDirectory();
        var source = System.IO.Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "bin"));
        File.WriteAllText(System.IO.Path.Combine(source, "file.cs"), "original");
        var hash = Evidence.SourceHash(source);
        File.WriteAllText(System.IO.Path.Combine(source, "bin", "ignored"), "noise");
        Assert.Equal(hash, Evidence.SourceHash(source));
        var copy = System.IO.Path.Combine(temp.Path, "copy");
        Evidence.CopyFixture(source, copy);
        Assert.Equal(hash, Evidence.SourceHash(copy));
        Assert.False(Directory.Exists(System.IO.Path.Combine(copy, "bin")));
        File.WriteAllText(System.IO.Path.Combine(copy, "file.cs"), "changed");
        Assert.NotEqual(hash, Evidence.SourceHash(copy));
    }

    [Fact]
    public void WaitUnionRejectsInvalidIntervals()
    {
        Assert.Equal(10, Evidence.WaitUnion([(0, 5), (3, 8), (10, 12)]));
        foreach (var interval in new[] { (3d, 2d), (-1d, 2d), (0d, double.PositiveInfinity) })
            Assert.Throws<InvalidDataException>(() => Evidence.WaitUnion([interval]));
    }

    [Fact]
    public void PairedReductionUsesMatchedBatchMeansAndNullUnsupportedMetrics()
    {
        var rows = new List<Trial>();
        foreach (var batch in new[] { "one", "two" })
            foreach (var condition in new[] { "dotnet", "candidate" })
                for (var index = 0; index < 2; index++)
                    rows.Add(new()
                    {
                        Workload = "unit",
                        Scenario = "repair",
                        BatchId = batch,
                        Condition = condition,
                        Correct = true,
                        EditToCorrectResultMs = condition == "dotnet" ? 100 : 70
                    });
        var reduction = Evidence.PairedChanges(rows, "candidate", 42);
        Assert.Equal(2, reduction["paired_batches"]);
        Assert.Equal(.3, reduction["median_relative"]);
        Assert.Equal(new[] { .3, .3 }, Assert.IsType<double[]>(reduction["bootstrap_95_percent_interval"]));
        Assert.Throws<InvalidDataException>(() => Evidence.PairedChanges(rows.SkipLast(1), "candidate"));
        rows[0].Correct = false;
        Assert.Throws<InvalidDataException>(() => Evidence.PairedChanges(rows, "candidate"));
        var summary = Evidence.Summarize(rows);
        Assert.Equal("pending", summary["G0"]);
        Assert.Null(summary["paired_improvement"]);
        Assert.Null(rows[0].AgentBlockedWaitMs);
    }

    [Theory]
    [InlineData("--workload", "integration")]
    [InlineData("--concurrency", "2")]
    [InlineData("--repetitions", "2")]
    [InlineData("--timeout", "NaN")]
    [InlineData("--concurrency", "8")]
    [InlineData("--timeout", "0")]
    public async Task PermissionGuardsDoNotCreateOutput(string option, string value)
    {
        using var temp = new TemporaryDirectory();
        var output = System.IO.Path.Combine(temp.Path, "not-created");
        Assert.Equal(2, await Program.Main(["--output", output, option, value]));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void OptionsRequireOutputAndRejectUnknownOrDuplicateArguments()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse([]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--output", "a", "--unknown"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--output", "a", "--output", "b"]));
        var options = Options.Parse(["--output", "a", "--scenario", "repair", "--concurrency", "4", "--approve-campaign"]);
        Assert.Equal(4, options.Concurrency);
        Assert.Equal("repair", options.Scenario);
        Assert.True(options.ApproveCampaign);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("///")]
    public void OutputSeparatorsAreNormalizedAndExperimentIdentityIsNonempty(string suffix)
    {
        using var temp = new TemporaryDirectory();
        var output = System.IO.Path.Combine(temp.Path, "experiment");
        var options = Options.Parse(["--output", output + suffix]);
        Assert.Equal(output, options.Output);
        Assert.Equal("experiment", System.IO.Path.GetFileName(options.Output));
    }

    [Fact]
    public void OutputDirectoryIsCreatedPrivatelyAndAtomically()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TemporaryDirectory();
        var output = System.IO.Path.Combine(temp.Path, "parent", "experiment");
        Program.CreateOutputDirectory(output);
        var mode = File.GetUnixFileMode(output);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
        Assert.Throws<IOException>(() => Program.CreateOutputDirectory(output));
        Assert.Equal(mode, File.GetUnixFileMode(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("/", false)]
    [InlineData("", true)]
    [InlineData("/", true)]
    public async Task ExistingOutputIsNeverOverwritten(string suffix, bool empty)
    {
        using var temp = new TemporaryDirectory();
        var marker = System.IO.Path.Combine(temp.Path, "marker");
        if (!empty) File.WriteAllText(marker, "retain");
        Assert.Equal(2, await Program.Main(["--output", temp.Path + suffix]));
        if (empty) Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
        else Assert.Equal("retain", File.ReadAllText(marker));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("///")]
    public async Task RootOutputIsPreservedAndRefused(string output)
    {
        var options = Options.Parse(["--output", output]);
        Assert.Equal("/", options.Output);
        Assert.Throws<IOException>(() => Program.CreateOutputDirectory(options.Output));
        Assert.Equal(2, await Program.Main(["--output", output]));
    }

    [Fact]
    public async Task ProcessExitTimeoutAndOwnedSampling()
    {
        using var temp = new TemporaryDirectory();
        var failed = await Processes.Command(["/bin/sh", "-c", "printf stdout; printf stderr >&2; exit 7"],
            temp.Path, temp.Path, "exit", 2);
        Assert.Equal(7, failed.ExitCode);
        Assert.Contains("stdout", File.ReadAllText(System.IO.Path.Combine(temp.Path, "exit.log")));
        Assert.Contains("stderr", File.ReadAllText(System.IO.Path.Combine(temp.Path, "exit.log")));
        var timeout = await Processes.Command(["sleep", "3"], temp.Path, temp.Path, "timeout", .1);
        Assert.True(timeout.TimedOut);
        Assert.NotEqual(0, timeout.ExitCode);
        Assert.True(timeout.ElapsedMs < 2000);
        Assert.True(timeout.PeakRssBytesSampled > 0);
    }

    [Fact]
    public async Task ProcessCancellationTerminatesOnlyOwnedTree()
    {
        using var temp = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Processes.Command(["/bin/sh", "-c", "sleep 30 & wait"], temp.Path,
                temp.Path, "cancel", 60, cancellationToken: cancellation.Token));
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("regression")]
    [InlineData("rounding")]
    [InlineData("known-failure")]
    [InlineData("regression-with-existing-failure")]
    [InlineData("repair")]
    [InlineData("compile-error")]
    public async Task ConcurrentTrialsPreserveScenarioOracleAndWorkspaceIsolation(string scenarioName)
    {
        using var temp = new TemporaryDirectory();
        var output = System.IO.Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(output);
        var workload = FixtureManifest.Load(System.IO.Path.Combine(Root, "scenarios.json")).Workloads["unit"];
        var baseline = new Dictionary<string, string>();
        foreach (var name in workload.Scenarios.Values.SelectMany(scenario => scenario.FailedSuffixes)
                     .Concat(workload.Scenarios.Values.SelectMany(scenario => scenario.BaselineFailedSuffixes)).Distinct())
            baseline["Tests." + name] = "Passed";
        for (var index = baseline.Count; index < workload.MinimumTests; index++)
            baseline["Tests.Case" + index] = "Passed";
        var options = new Options("unit", scenarioName, output, 2, ApproveCampaign: true);
        var scenario = workload.Scenarios[scenarioName];
        Task<CommandResult> Execute(string[] argv, string cwd, string artifacts, string label,
            double timeout, Dictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            var results = argv.Contains("--results-directory");
            if (results)
            {
                var directory = argv[Array.IndexOf(argv, "--results-directory") + 1];
                if (label == "edited" && scenario.Diagnostic is not null)
                    File.WriteAllText(System.IO.Path.Combine(artifacts, "edited.log"), "error CS0103");
                else
                {
                    var expected = Evidence.ExpectedResults(baseline, scenario.BaselineFailedSuffixes);
                    if (label == "repair-preparation")
                        expected = Evidence.ExpectedResults(expected, workload.Scenarios["regression"].FailedSuffixes);
                    if (label == "edited")
                        expected = Evidence.ExpectedResults(expected, scenario.FailedSuffixes);
                    WriteTrx(directory, expected);
                    return Task.FromResult(new CommandResult(argv, 1, expected.Values.Contains("Failed") ? 1 : 0,
                        false, 1, 0, label + ".log", 0, 0));
                }
            }
            return Task.FromResult(new CommandResult(argv, 1,
                label == "edited" && scenario.Diagnostic is not null ? 1 : 0,
                false, 1, 0, label + ".log", 0, 0));
        }
        var barrier = new EditBarrier(2);
        var records = await Task.WhenAll(Enumerable.Range(0, 2).Select(index =>
            Trials.Run(Root, options, workload, "batch", index, barrier, execute: Execute)));
        Assert.All(records, record =>
        {
            Assert.True(record.Correct, record.Error);
            Assert.NotEqual(record.PreEditHash, record.EditHash);
            Assert.True(record.EditToCorrectResultMs >= 0);
            var lines = File.ReadAllLines(System.IO.Path.Combine(record.ArtifactDirectory, "events.jsonl"));
            var times = lines.Select(line => JsonNode.Parse(line)!["elapsed_ns"]!.GetValue<long>()).ToArray();
            Assert.Equal(times.Order(), times);
        });
        Assert.NotEqual(records[0].ArtifactDirectory, records[1].ArtifactDirectory);
    }

    [Fact]
    public async Task FailedSetupAbortsPeerBarrierAndRetainsInvalidAttempts()
    {
        using var temp = new TemporaryDirectory();
        var workload = FixtureManifest.Load(System.IO.Path.Combine(Root, "scenarios.json")).Workloads["unit"];
        var options = new Options("unit", "baseline", temp.Path, 2, Timeout: 1, ApproveCampaign: true);
        Task<CommandResult> Execute(string[] argv, string cwd, string artifacts, string label,
            double timeout, Dictionary<string, string>? environment, CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResult(argv, 1, 1, true, 1, 0, label + ".log", 0, 0));
        var barrier = new EditBarrier(2);
        var rows = await Task.WhenAll(Enumerable.Range(0, 2).Select(index =>
            Trials.Run(Root, options, workload, "batch", index, barrier, execute: Execute)));
        Assert.All(rows, row =>
        {
            Assert.False(row.Correct);
            Assert.True(row.TimedOut);
            Assert.Null(row.EditToCorrectResultMs);
            Assert.Single(row.Incidents);
            Assert.True(File.Exists(System.IO.Path.Combine(row.ArtifactDirectory, "trial.json")));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => barrier.Wait(1, CancellationToken.None));
    }

    internal static void WriteTrx(string directory, Dictionary<string, string> outcomes)
    {
        System.Xml.Linq.XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var rows = outcomes.Select((pair, index) => (pair.Key, pair.Value, Id: index.ToString())).ToArray();
        var tree = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement(ns + "TestRun",
            new System.Xml.Linq.XElement(ns + "TestDefinitions", rows.Select(row =>
                new System.Xml.Linq.XElement(ns + "UnitTest", new System.Xml.Linq.XAttribute("id", row.Id),
                    new System.Xml.Linq.XElement(ns + "TestMethod", new System.Xml.Linq.XAttribute("className", "Tests, Assembly"))))),
            new System.Xml.Linq.XElement(ns + "Results", rows.Select(row =>
                new System.Xml.Linq.XElement(ns + "UnitTestResult",
                    new System.Xml.Linq.XAttribute("testId", row.Id), new System.Xml.Linq.XAttribute("testName", row.Key),
                    new System.Xml.Linq.XAttribute("outcome", row.Value)))),
            new System.Xml.Linq.XElement(ns + "ResultSummary", new System.Xml.Linq.XElement(ns + "Counters",
                new System.Xml.Linq.XAttribute("total", rows.Length)))));
        tree.Save(System.IO.Path.Combine(directory, "test.trx"));
    }
}
