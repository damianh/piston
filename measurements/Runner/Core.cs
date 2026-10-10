using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Piston.Measurements;

public static class Evidence
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
    public static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };

    public static long NowNs => (long)(Stopwatch.GetTimestamp() * (1_000_000_000d / Stopwatch.Frequency));
    public static long WallNs => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;
    public static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Digest(string value) => Digest(Encoding.UTF8.GetBytes(value));
    public static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + "\n");
    public static void WriteLines<T>(string path, IEnumerable<T> values) =>
        File.WriteAllLines(path, values.Select(value => JsonSerializer.Serialize(value, JsonLine)));

    public static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or "TestResults"))
            // Preserve the original runner's component-wise pathlib ordering.
            .OrderBy(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '\0'),
                StringComparer.Ordinal);

    public static string SourceHash(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in SourceFiles(root))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path)));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(path));
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static void CopyFixture(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in SourceFiles(source))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public static Dictionary<string, object?> HostContext()
    {
        var membership = File.ReadAllText("/proc/self/cgroup");
        var unified = membership.Split('\n').FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal));
        var limits = new Dictionary<string, Dictionary<string, string>>();
        if (unified is not null)
        {
            const string root = "/sys/fs/cgroup";
            var current = Path.GetFullPath(Path.Combine(root, unified[3..].TrimStart('/')));
            while (current == root || current.StartsWith(root + "/", StringComparison.Ordinal))
            {
                var files = new Dictionary<string, string>();
                foreach (var name in new[] { "cpu.max", "memory.max", "memory.high" })
                    if (File.Exists(Path.Combine(current, name)))
                        files[name] = File.ReadAllText(Path.Combine(current, name)).Trim();
                if (files.Count > 0)
                    limits[Path.GetRelativePath(root, current)] = files;
                if (current == root) break;
                current = Path.GetDirectoryName(current)!;
            }
        }
        return new()
        {
            ["load_average"] = File.ReadAllText("/proc/loadavg").Split(' ').Take(3)
                .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray(),
            ["host_memory"] = File.ReadAllText("/proc/meminfo"),
            ["host_pressure_context"] = new[] { "cpu", "memory", "io" }
                .ToDictionary(kind => kind, kind => File.ReadAllText($"/proc/pressure/{kind}")),
            ["runner_cgroup_membership"] = membership,
            ["cgroup_ancestor_limits"] = limits
        };
    }

    public static Dictionary<string, string> ParseTrx(string directory, long startedNs = 0)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var files = Directory.GetFiles(directory, "*.trx").Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new InvalidDataException("Missing TRX results");
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if ((File.GetLastWriteTimeUtc(file).Ticks - DateTime.UnixEpoch.Ticks) * 100 < startedNs)
                throw new InvalidDataException("Stale TRX results");
            var tree = XDocument.Load(file);
            var definitions = tree.Descendants(ns + "UnitTest").ToDictionary(
                test => Attribute(test, "id"),
                test => Attribute(test.Element(ns + "TestMethod")
                    ?? throw new InvalidDataException("Missing test definition"), "className").Split(',')[0]);
            var local = tree.Descendants(ns + "UnitTestResult").ToArray();
            var counters = tree.Descendants(ns + "Counters").FirstOrDefault();
            if (counters is null || int.Parse(Attribute(counters, "total"), CultureInfo.InvariantCulture) != local.Length)
                throw new InvalidDataException("Incomplete TRX counters");
            foreach (var result in local)
            {
                var className = definitions[Attribute(result, "testId")];
                var name = Attribute(result, "testName");
                var identity = name.StartsWith(className + ".", StringComparison.Ordinal) ? name : className + "." + name;
                var outcome = Attribute(result, "outcome");
                if (outcome is not ("Passed" or "Failed" or "NotExecuted"))
                    throw new InvalidDataException("Unsupported outcome: " + outcome);
                if (!results.TryAdd(identity, outcome))
                    throw new InvalidDataException("Duplicate test identity: " + identity);
            }
        }
        return results;
    }

    private static string Attribute(XElement element, string name) =>
        element.Attribute(name)?.Value ?? throw new InvalidDataException("Missing TRX attribute: " + name);

    public static Dictionary<string, string> ExpectedResults(
        IReadOnlyDictionary<string, string> baseline, IEnumerable<string> suffixes)
    {
        var expected = new Dictionary<string, string>(baseline, StringComparer.Ordinal);
        foreach (var suffix in suffixes)
        {
            var matches = baseline.Keys.Where(name => name.EndsWith(suffix, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("Sentinel must identify exactly one test: " + suffix);
            expected[matches[0]] = "Failed";
        }
        return expected;
    }

    public static void Oracle(IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> observed, int exitCode)
    {
        if (expected.Count != observed.Count ||
            expected.Any(pair => !observed.TryGetValue(pair.Key, out var outcome) || outcome != pair.Value))
            throw new InvalidDataException("Test identities/statuses disagree with full-scope oracle");
        var expectedExit = expected.Values.Contains("Failed") ? 1 : 0;
        if (exitCode != expectedExit)
            throw new InvalidDataException($"Unexpected exit code {exitCode}, expected {expectedExit}");
    }

    public static double WaitUnion(IEnumerable<(double Start, double End)> intervals)
    {
        double total = 0, start = 0, end = 0;
        foreach (var interval in intervals.OrderBy(interval => interval.Start))
        {
            if (interval.Start < 0 || interval.End < interval.Start ||
                !double.IsFinite(interval.Start + interval.End))
                throw new InvalidDataException("Invalid blocked-wait interval");
            if (interval.Start > end)
            {
                total += end - start;
                start = interval.Start;
            }
            end = Math.Max(end, interval.End);
        }
        return total + end - start;
    }

    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new InvalidDataException("No observations");
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }

    public static Dictionary<string, object?> PairedChanges(IEnumerable<Trial> records, string candidate, int seed = 0)
    {
        var rows = records.ToArray();
        if (candidate == "dotnet" || rows.Select(row => (row.Workload, row.Scenario)).Distinct().Count() != 1)
            throw new InvalidDataException("Pair one workload/scenario and a distinct candidate at a time");
        if (rows.Any(row => !row.Correct))
            throw new InvalidDataException("Correctness failures disqualify paired benefit calculation");
        if (rows.Any(row => row.EditToCorrectResultMs is not double value || value <= 0 || !double.IsFinite(value)))
            throw new InvalidDataException("Positive finite latency required");
        var changes = new List<(double Absolute, double Relative)>();
        foreach (var batch in rows.GroupBy(row => row.BatchId))
        {
            var conditions = batch.GroupBy(row => row.Condition).ToDictionary(group => group.Key, group => group.ToArray());
            if (conditions.Count != 2 || !conditions.ContainsKey("dotnet") || !conditions.ContainsKey(candidate))
                throw new InvalidDataException("Each batch needs both matched conditions");
            if (conditions["dotnet"].Length != conditions[candidate].Length)
                throw new InvalidDataException("Concurrency differs between paired conditions");
            var baseline = conditions["dotnet"].Average(row => row.EditToCorrectResultMs!.Value);
            var compared = conditions[candidate].Average(row => row.EditToCorrectResultMs!.Value);
            changes.Add((baseline - compared, (baseline - compared) / baseline));
        }
        var relative = changes.Select(change => change.Relative).ToArray();
        double[]? interval = null;
        if (changes.Count >= 2)
        {
            var random = new Random(seed);
            var samples = Enumerable.Range(0, 2000).Select(_ =>
                Median(Enumerable.Range(0, relative.Length).Select(_ => relative[random.Next(relative.Length)])))
                .Order().ToArray();
            interval = [samples[49], samples[1949]];
        }
        return new()
        {
            ["paired_batches"] = changes.Count,
            ["median_absolute_ms"] = Median(changes.Select(change => change.Absolute)),
            ["median_relative"] = Median(relative),
            ["bootstrap_95_percent_interval"] = interval,
            ["seed"] = seed,
            ["uncertainty_reason"] = interval is null ? "Too few independent batches" : null
        };
    }

    public static Dictionary<string, object?> Summarize(IEnumerable<Trial> records)
    {
        var groups = records.GroupBy(row => (row.Condition, row.Workload, row.Scenario)).Select(group =>
        {
            var rows = group.ToArray();
            var values = rows.Where(row => row.Correct && row.EditToCorrectResultMs.HasValue)
                .Select(row => row.EditToCorrectResultMs!.Value).Order().ToArray();
            return new Dictionary<string, object?>
            {
                ["condition"] = group.Key.Condition,
                ["workload"] = group.Key.Workload,
                ["scenario"] = group.Key.Scenario,
                ["attempted"] = rows.Length,
                ["valid"] = values.Length,
                ["invalid"] = rows.Length - values.Length,
                ["timeouts"] = rows.Count(row => row.TimedOut),
                ["invalid_reasons"] = rows.Where(row => !row.Correct).Select(row => row.Error ?? "No valid latency").ToArray(),
                ["incidents"] = rows.Sum(row => row.Incidents.Count),
                ["batches"] = rows.Select(row => row.BatchId).Distinct().Count(),
                ["median_ms"] = values.Length > 0 ? Median(values) : (double?)null,
                ["p95_ms"] = values.Length > 0 ? values[(int)Math.Ceiling(.95 * values.Length) - 1] : (double?)null
            };
        }).ToArray();
        return new()
        {
            ["schema_version"] = 1,
            ["G0"] = "pending",
            ["groups"] = groups,
            ["paired_improvement"] = null,
            ["uncertainty"] = null,
            ["reason"] = "Baseline instrumentation only; no validated backend or real-agent comparison"
        };
    }
}

public sealed class Scenario
{
    public required string Before { get; init; }
    public required string After { get; init; }
    public required string[] FailedSuffixes { get; init; }
    public string? SeedBefore { get; init; }
    public string? SeedAfter { get; init; }
    public string[] BaselineFailedSuffixes { get; init; } = [];
    public bool Repair { get; init; }
    public string? Diagnostic { get; init; }
}

public sealed class Workload
{
    public required string Solution { get; init; }
    public required string Source { get; init; }
    public required int MinimumTests { get; init; }
    public required Dictionary<string, Scenario> Scenarios { get; init; }
}

public sealed class FixtureManifest
{
    public required int SchemaVersion { get; init; }
    public required Dictionary<string, Workload> Workloads { get; init; }

    public static FixtureManifest Load(string path)
    {
        var result = JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(path), Evidence.Json)
            ?? throw new InvalidDataException("Missing workloads manifest");
        if (result.SchemaVersion != 1 || result.Workloads is null)
            throw new InvalidDataException("Expected version 1 workloads manifest");
        foreach (var workload in result.Workloads.Values)
        {
            foreach (var relative in new[] { workload.Solution, workload.Source })
                if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains(".."))
                    throw new InvalidDataException("Fixture paths must be relative and contained");
            if (workload.MinimumTests < 1)
                throw new InvalidDataException("minimum_tests must be positive");
            foreach (var scenario in workload.Scenarios.Values)
                if (string.IsNullOrEmpty(scenario.Before) || scenario.Before == scenario.After ||
                    scenario.FailedSuffixes is null || (scenario.SeedBefore is not null && scenario.SeedAfter is null))
                    throw new InvalidDataException("Each scenario needs a real edit and failed_suffixes");
        }
        return result;
    }
}

public sealed record Options(string Workload, string Scenario, string Output, int Concurrency = 1,
    int Repetitions = 1, double Timeout = 180, bool ApproveCampaign = false, bool ApproveContainers = false)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var names = new[] { "--workload", "--scenario", "--output", "--concurrency", "--repetitions", "--timeout" };
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--approve-campaign" or "--approve-containers")
            {
                if (!flags.Add(args[i])) throw new ArgumentException("Duplicate option: " + args[i]);
            }
            else if (names.Contains(args[i]) && i + 1 < args.Length)
            {
                if (!values.TryAdd(args[i], args[++i])) throw new ArgumentException("Duplicate option");
            }
            else throw new ArgumentException("Unknown or incomplete option: " + args[i]);
        }
        if (!values.TryGetValue("--output", out var output) || string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("--output is required");
        return new(values.GetValueOrDefault("--workload", "unit"),
            values.GetValueOrDefault("--scenario", "baseline"), Path.GetFullPath(output),
            int.Parse(values.GetValueOrDefault("--concurrency", "1"), CultureInfo.InvariantCulture),
            int.Parse(values.GetValueOrDefault("--repetitions", "1"), CultureInfo.InvariantCulture),
            double.Parse(values.GetValueOrDefault("--timeout", "180"), CultureInfo.InvariantCulture),
            flags.Contains("--approve-campaign"), flags.Contains("--approve-containers"));
    }

    public Workload Validate(FixtureManifest manifest)
    {
        if (!manifest.Workloads.TryGetValue(Workload, out var workload) ||
            !workload.Scenarios.ContainsKey(Scenario))
            throw new ArgumentException("Unknown workload/scenario");
        if (Concurrency is not (1 or 2 or 4) || Repetitions < 1 || Timeout <= 0 || !double.IsFinite(Timeout) ||
            Timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1).TotalSeconds)
            throw new ArgumentException("Concurrency must be 1, 2 or 4; timeout/repetitions must be positive and finite");
        if ((Concurrency > 1 || Repetitions > 1) && !ApproveCampaign)
            throw new ArgumentException("Repeated/concurrent runs require explicit --approve-campaign");
        if (Workload == "integration")
        {
            if (!ApproveContainers)
                throw new ArgumentException("Integration execution requires explicit --approve-containers");
            if (!Regex.IsMatch(Environment.GetEnvironmentVariable("MEASUREMENT_POSTGRES_IMAGE") ?? "",
                    @"\Apostgres@sha256:[0-9a-f]{64}\z"))
                throw new ArgumentException("Set MEASUREMENT_POSTGRES_IMAGE to an approved immutable digest");
            ContainerApi.ValidateHost(Environment.GetEnvironmentVariable("DOCKER_HOST"));
        }
        return workload;
    }
}
