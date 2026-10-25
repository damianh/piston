// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Server-mode (JSON-RPC) execution of MTP test apps via <see cref="MtpServerSession"/>: tests stream in
/// as they are discovered, and each test reports <see cref="TestStatus.Running"/> while it executes.
/// Filters are evaluated locally against discovered nodes, then only matching nodes are run.
/// </summary>
internal static partial class MtpTestProcessRunner
{
    private const string HostExitedMessage = "The test host exited while this test was running.";

    /// <summary>
    /// Discovers tests over the server protocol. Returns <see langword="null"/> when server mode is
    /// unavailable so the caller can fall back to <c>dotnet test --list-tests</c>.
    /// </summary>
    internal static async Task<IReadOnlyList<TestResult>?> DiscoverWithServerAsync(
        string appPath,
        string solutionDirectory,
        TestFilterExpression? filter,
        Action<IReadOnlyList<TestResult>>? onPartial,
        CancellationToken ct,
        MtpWarmHostPool? warmHosts = null)
    {
        var tests = new List<TestResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var gate = new object();
        using var partials = onPartial is null
            ? null
            : new TrailingThrottle(
                () =>
                {
                    IReadOnlyList<TestResult> snapshot;
                    lock (gate)
                    {
                        snapshot = [.. tests];
                    }

                    onPartial(snapshot);
                },
                TestProcessRunner.ProgressInterval);

        var result = await MtpServerSession.RunAsync(appPath, solutionDirectory, MtpServerSession.DiscoverMethod, tests: null, nodes =>
        {
            bool added;
            lock (gate)
            {
                added = AddDiscovered(tests, seen, nodes, filter, appPath);
            }

            if (added)
            {
                partials?.Signal();
            }
        }, ct, warmHosts: warmHosts).ConfigureAwait(false);

        partials?.Dispose();
        DiagnosticLog.Instance?.Write("MtpRunner",
            $"Server discovery {result.Outcome}: {tests.Count} test(s) in {appPath}" + (result.Error is null ? "" : $" | {result.Error}"));

        // A rejected request completes with an error: its list may be empty or partial, so fall back.
        if (result.Outcome != MtpServerSession.Outcome.Cancelled
            && (result.Outcome != MtpServerSession.Outcome.Completed || result.Error is not null))
        {
            return null;
        }

        lock (gate)
        {
            return [.. tests];
        }
    }

    /// <summary>
    /// Runs tests over the server protocol, reporting live per-test status. Returns <see langword="null"/>
    /// when server mode is unavailable or the filter is unsupported, before any test has run, so the
    /// caller can fall back to <c>dotnet test</c> without running tests twice.
    /// </summary>
    internal static async Task<ProjectTestResult?> RunWithServerAsync(
        string projectPath,
        string appPath,
        string solutionDirectory,
        string? filter,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct,
        ExecutionLog? runLog = null,
        MtpWarmHostPool? warmHosts = null)
    {
        var log = runLog ?? new ExecutionLog(projectPath);
        log.Runner = "MTP server";
        IReadOnlyList<JsonElement>? selection = null;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            if (!TestFilterExpression.TryParse(filter, out var expression))
            {
                log.Write("MtpRunner", $"Unsupported filter for server mode, using dotnet test: {filter}");
                return null;
            }

            var discovered = new List<JsonElement>();
            var discoveredKeys = new HashSet<string>(StringComparer.Ordinal);
            var discovery = await MtpServerSession.RunAsync(appPath, solutionDirectory, MtpServerSession.DiscoverMethod, tests: null,
                nodes => discovered.AddRange(nodes.Where(n => IsAction(n) && NodeKey(n) is { } key && discoveredKeys.Add(key))),
                ct, log, warmHosts).ConfigureAwait(false);
            if (discovery.Outcome == MtpServerSession.Outcome.Cancelled)
            {
                return new ProjectTestResult(projectPath, [], null, [], Crashed: false);
            }

            if (discovery.Outcome != MtpServerSession.Outcome.Completed || discovery.Error is not null)
            {
                log.Write("MtpRunner", $"Server discovery {discovery.Outcome} ({discovery.Error}); using dotnet test");
                return null;
            }

            var matched = discovered.Where(n => Matches(expression, n)).ToList();
            log.Write("MtpRunner", $"Filter matched {matched.Count} of {discovered.Count} test(s): {filter}");
            if (matched.Count == 0)
            {
                return new ProjectTestResult(projectPath, [], null, [], Crashed: false);
            }

            selection = matched.Count == discovered.Count ? null : matched;
        }

        var live = new Dictionary<string, TestResult>(StringComparer.Ordinal);
        var gate = new object();

        IReadOnlyList<TestResult> Snapshot()
        {
            lock (gate)
            {
                return [.. live.Values];
            }
        }

        using var progress = onProgress is null
            ? null
            : new TrailingThrottle(
                () =>
                {
                    var tests = Snapshot();
                    onProgress(tests.Count == 0 ? [] : [new TestSuite("…", tests, DateTimeOffset.UtcNow, TimeSpan.Zero)]);
                },
                TestProcessRunner.ProgressInterval);

        var run = await MtpServerSession.RunAsync(appPath, solutionDirectory, MtpServerSession.RunMethod, selection, nodes =>
        {
            lock (gate)
            {
                ApplyRunUpdates(live, nodes, appPath);
            }

            progress?.Signal();
        }, ct, log, warmHosts).ConfigureAwait(false);

        log.Write("MtpRunner", $"Server run {run.Outcome}: {live.Count} test(s)" + (run.Error is null ? "" : $" | {run.Error}"));

        switch (run.Outcome)
        {
            case MtpServerSession.Outcome.NotStarted:
            case MtpServerSession.Outcome.TimedOut:
                progress?.Dispose();
                return null;
            case MtpServerSession.Outcome.Cancelled:
                progress?.Dispose();
                return new ProjectTestResult(projectPath, [], null, [], Crashed: false);
            case MtpServerSession.Outcome.Failed:
                lock (gate)
                {
                    foreach (var (key, test) in live.Where(kv => kv.Value.Status == TestStatus.Running).ToList())
                    {
                        live[key] = test with { Status = TestStatus.Failed, ErrorMessage = HostExitedMessage };
                    }
                }

                break;
            case MtpServerSession.Outcome.Completed:
                break;
            default:
                break;
        }

        progress?.Flush();
        progress?.Dispose();

        var final = Snapshot();
        var suites = final.Count == 0
            ? []
            : new List<TestSuite>
            {
                new(TestProjectNames.SuiteName(projectPath), final, DateTimeOffset.UtcNow,
                    TimeSpan.FromTicks(final.Sum(t => t.Duration.Ticks))),
            };

        string? runnerError = null;
        if (run.Error is not null)
        {
            runnerError = run.Stderr.Count == 0
                ? run.Error
                : run.Error + Environment.NewLine + string.Join(Environment.NewLine, run.Stderr);
            log.Write("MtpRunner", $"RunnerError: {runnerError}");
        }

        return new ProjectTestResult(projectPath, suites, runnerError, [],
            Crashed: run.Outcome == MtpServerSession.Outcome.Failed)
        { Log = log };
    }

    // ── Test node mapping ───────────────────────────────────────────────────

    /// <summary>
    /// Adds newly discovered test nodes that match <paramref name="filter"/>. Nodes are deduplicated by
    /// UID (repeated updates for one node), not display name: distinct tests may share a display name.
    /// Returns whether any test was added.
    /// </summary>
    internal static bool AddDiscovered(
        List<TestResult> tests,
        HashSet<string> seen,
        IReadOnlyList<JsonElement> nodes,
        TestFilterExpression? filter,
        string appPath)
    {
        var added = false;
        foreach (var node in nodes)
        {
            if (IsAction(node) && Matches(filter, node) && NodeKey(node) is { } key && seen.Add(key))
            {
                tests.Add(ToTestResult(node, appPath, TestStatus.NotRun));
                added = true;
            }
        }

        return added;
    }

    /// <summary>
    /// Records execution updates keyed by node UID, so distinct tests sharing a display name each keep
    /// their own result (and surface as duplicate test IDs) instead of overwriting each other.
    /// </summary>
    internal static void ApplyRunUpdates(Dictionary<string, TestResult> live, IReadOnlyList<JsonElement> nodes, string appPath)
    {
        foreach (var node in nodes)
        {
            if (!IsAction(node) || NodeKey(node) is not { } key || IsSupersededRetry(node))
            {
                continue;
            }

            var status = MapState(StringProperty(node, "execution-state"));
            if (status == TestStatus.NotRun && live.ContainsKey(key))
            {
                continue;
            }

            live[key] = ToTestResult(node, appPath, status);
        }
    }

    internal static TestStatus MapState(string? state) => state switch
    {
        "in-progress" => TestStatus.Running,
        "passed" => TestStatus.Passed,
        "skipped" => TestStatus.Skipped,
        "failed" or "timed-out" or "error" => TestStatus.Failed,
        _ => TestStatus.NotRun,
    };

    internal static TestResult ToTestResult(JsonElement node, string appPath, TestStatus status)
    {
        var name = DisplayName(node) ?? string.Empty;
        var duration = node.TryGetProperty("time.duration-ms", out var ms) && ms.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromMilliseconds(ms.GetDouble())
            : TimeSpan.Zero;

        var message = StringProperty(node, "error.message");
        var expected = StringProperty(node, "assert.expected");
        var actual = StringProperty(node, "assert.actual");
        if (message is not null && (expected is not null || actual is not null))
        {
            message += $"{Environment.NewLine}Expected: {expected}{Environment.NewLine}Actual:   {actual}";
        }

        var output = string.Join(Environment.NewLine,
            new[] { StringProperty(node, "standardOutput"), StringProperty(node, "standardError") }
                .Where(s => !string.IsNullOrEmpty(s)));

        return new TestResult(
            name,
            TestProcessRunner.DisplayNameOf(name),
            status,
            duration,
            output.Length == 0
                ? null
                : RetentionLimits.TruncateHeadTail(output, RetentionLimits.MaxTestTextLength),
            RetentionLimits.TruncateHead(message, RetentionLimits.MaxTestTextLength),
            RetentionLimits.TruncateHead(StringProperty(node, "error.stacktrace"), RetentionLimits.MaxTestTextLength),
            appPath)
        {
            Method = MethodOf(node),
        };
    }

    /// <summary>The node's test method, <c>location.type</c> and <c>location.method</c> less its parameter list.</summary>
    private static string? MethodOf(JsonElement node) =>
        StringProperty(node, "location.type") is { Length: > 0 } type
        && StringProperty(node, "location.method") is { Length: > 0 } method
            ? $"{type}.{(method.IndexOf('(', StringComparison.Ordinal) is var paren and >= 0 ? method[..paren] : method)}"
            : null;

    private static bool IsAction(JsonElement node) => StringProperty(node, "node-type") == "action";

    private static string? DisplayName(JsonElement node) =>
        StringProperty(node, "display-name") is { Length: > 0 } name ? name : null;

    /// <summary>The node's identity: its UID, or its display name when the host omits the UID.</summary>
    private static string? NodeKey(JsonElement node) =>
        DisplayName(node) is null ? null : StringProperty(node, "uid") is { Length: > 0 } uid ? uid : DisplayName(node);

    private static bool IsSupersededRetry(JsonElement node) =>
        node.TryGetProperty("retry.is-superseded", out var superseded) && superseded.ValueKind == JsonValueKind.True;

    private static string? StringProperty(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Matches(TestFilterExpression? filter, JsonElement node) =>
        filter is null || filter.Matches(property => FilterValues(node, property));

    /// <summary>The values a VSTest filter property can match for an MTP test node.</summary>
    internal static IReadOnlyList<string> FilterValues(JsonElement node, string property)
    {
        var display = DisplayName(node);
        var type = StringProperty(node, "location.type");
        var method = StringProperty(node, "location.method") is { } m && m.IndexOf('(', StringComparison.Ordinal) is var paren and >= 0
            ? m[..paren]
            : StringProperty(node, "location.method");

        var values = new List<string>();
        void Add(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                values.Add(value);
            }
        }

        switch (property.ToLowerInvariant())
        {
            case "fullyqualifiedname":
                Add(display);
                Add(type is not null && method is not null ? $"{type}.{method}" : null);
                break;
            case "name":
            case "displayname":
                Add(display);
                Add(method);
                break;
            case "classname":
                Add(type);
                break;
            default:
                foreach (var (key, value) in Traits(node))
                {
                    if (key.Equals(property, StringComparison.OrdinalIgnoreCase)
                        || (IsCategory(key) && IsCategory(property)))
                    {
                        Add(value);
                    }
                }

                break;
        }

        return values;
    }

    private static bool IsCategory(string name) =>
        name.Equals("Category", StringComparison.OrdinalIgnoreCase)
        || name.Equals("TestCategory", StringComparison.OrdinalIgnoreCase);

    // Traits arrive as {"key": k, "value": v} objects or as single-entry {k: v} maps.
    private static IEnumerable<(string Key, string? Value)> Traits(JsonElement node)
    {
        if (!node.TryGetProperty("traits", out var traits) || traits.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var trait in traits.EnumerateArray())
        {
            if (trait.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (trait.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String)
            {
                yield return (key.GetString()!, StringProperty(trait, "value"));
                continue;
            }

            foreach (var entry in trait.EnumerateObject())
            {
                yield return (entry.Name, entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null);
            }
        }
    }
}
