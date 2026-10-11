using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Piston.Measurements;

/// <summary>
/// Independent re-implementation of Piston's <c>piston-source-v1</c> source-input fingerprint.
/// Deliberately does not reference the Piston engine so the harness can check Piston's claim.
/// </summary>
public static class PistonSourceFingerprint
{
    public const string Algorithm = "piston-source-v1";

    private static readonly string[] Excluded = ["bin", "obj", "TestResults", ".git", ".vs", ".idea", ".piston", "node_modules"];
    private static readonly string[] Ancestors =
    [
        "global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp",
        "Directory.Packages.props", "NuGet.config", "nuget.config",
    ];

    /// <summary>Fingerprint for a solution: inputs are rooted at the solution's directory.</summary>
    public static string ForSolution(string solutionPath) =>
        Compute(Path.GetDirectoryName(Path.GetFullPath(solutionPath))!);

    public static string Compute(string root)
    {
        root = Path.GetFullPath(root);
        var inputs = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Name: Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), Path: path))
            .Where(input => !input.Name.Split('/').Any(part => Excluded.Contains(part, StringComparer.Ordinal)))
            .ToList();
        foreach (var name in Ancestors.Where(name => !File.Exists(Path.Combine(root, name))))
        {
            var prefix = "";
            for (var directory = Directory.GetParent(root); directory is not null; directory = directory.Parent)
            {
                prefix += "../";
                if (!File.Exists(Path.Combine(directory.FullName, name))) continue;
                inputs.Add((prefix + name, Path.Combine(directory.FullName, name)));
                break;
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(Algorithm + "\0"));
        foreach (var input in inputs.OrderBy(input => input.Name.Replace('/', '\0'), StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(input.Name + "\0"));
            hash.AppendData(File.ReadAllBytes(input.Path));
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

/// <summary>
/// Full-scope oracle for a Piston <c>GetTestRun</c> (<c>piston.test-run/v1</c>) document. A run is accepted
/// only if it is the requested run, completed, unfiltered full scope, current for the harness-computed
/// source fingerprint, and reports exactly the expected unique test identities and statuses.
/// Summary counts are never accepted as evidence.
/// </summary>
public static class PistonRunOracle
{
    public const string Schema = "piston.test-run/v1";

    /// <returns>Observed identity → TRX-style outcome (Passed/Failed/NotExecuted).</returns>
    /// <exception cref="InvalidDataException">The run cannot satisfy the oracle.</exception>
    public static Dictionary<string, string> Validate(string runJson, string expectedRunId,
        string expectedSourceFingerprint, IReadOnlyDictionary<string, string> expected)
    {
        JsonObject run;
        try
        {
            run = JsonNode.Parse(runJson)?.AsObject() ?? throw Reject("run document is empty");
        }
        catch (JsonException ex)
        {
            throw Reject("run document is not JSON: " + ex.Message);
        }

        Require(Text(run, "schema") == Schema, "unsupported schema " + Text(run, "schema"));
        Require(!string.IsNullOrEmpty(expectedRunId), "expected run ID is missing");
        Require(Text(run, "runId") == expectedRunId,
            $"run ID {Text(run, "runId")} is not the requested run {expectedRunId}");
        Require(Bool(run, "isTerminal"), "run has not finished");
        Require(Text(run, "status") == "Completed",
            $"run status is {Text(run, "status")} ({Text(run, "statusReason")})");
        Require(Text(run, "scope") == "Full", $"run scope is {Text(run, "scope") ?? "unknown"}, not Full");
        Require(!Bool(run, "isFiltered") && run["context"]?["effectiveFilter"] is null,
            "run was filtered: " + run["context"]?["effectiveFilter"]);
        Require(Bool(run, "isFullScope"), "run is not full scope");
        Require(Text(run, "freshness") == "Current",
            $"run freshness is {Text(run, "freshness")} ({Text(run, "freshnessReason")})");
        Require(!Bool(run, "sourceChangeObserved"), "a source change was observed during the run");
        foreach (var key in new[] { "sourceBefore", "sourceAfter" })
        {
            var fingerprint = run[key] as JsonObject ?? throw Reject(key + " fingerprint is missing");
            Require(Text(fingerprint, "algorithm") == PistonSourceFingerprint.Algorithm,
                $"{key} uses unsupported algorithm {Text(fingerprint, "algorithm")}");
            Require(Text(fingerprint, "value") == expectedSourceFingerprint,
                $"{key} fingerprint {Text(fingerprint, "value")} does not match expected source {expectedSourceFingerprint}");
        }
        Require(run["duplicateTestIds"] is JsonArray { Count: 0 }, "run reported duplicate test identities");
        Require(Bool(run, "isCurrentFullResult"), "Piston does not mark the run as a current full result");

        if (run["tests"] is not JsonArray tests)
            throw Reject("per-test results are missing; summary counts are not provenance");

        var observed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var test in tests)
        {
            var name = test?["fullyQualifiedName"]?.GetValue<string>();
            Require(!string.IsNullOrWhiteSpace(name), "test identity is missing");
            var outcome = test!["status"]?.GetValue<string>() switch
            {
                "Passed" => "Passed",
                "Failed" => "Failed",
                "Skipped" => "NotExecuted",
                var other => throw Reject($"unsupported status {other} for {name}"),
            };
            Require(observed.TryAdd(name!, outcome), "duplicate test identity: " + name);
        }

        var missing = expected.Keys.Where(name => !observed.ContainsKey(name)).Order(StringComparer.Ordinal).ToArray();
        var extra = observed.Keys.Where(name => !expected.ContainsKey(name)).Order(StringComparer.Ordinal).ToArray();
        var mismatched = expected.Where(pair => observed.TryGetValue(pair.Key, out var outcome) && outcome != pair.Value)
            .Select(pair => $"{pair.Key} expected {pair.Value} got {observed[pair.Key]}")
            .Order(StringComparer.Ordinal).ToArray();
        Require(missing.Length == 0, $"{missing.Length} expected test(s) missing, e.g. {missing.FirstOrDefault()}");
        Require(extra.Length == 0, $"{extra.Length} unexpected test(s), e.g. {extra.FirstOrDefault()}");
        Require(mismatched.Length == 0, $"{mismatched.Length} status mismatch(es), e.g. {mismatched.FirstOrDefault()}");
        return observed;
    }

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Bool(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw Reject(reason);
    }

    private static InvalidDataException Reject(string reason) => new("Piston run rejected: " + reason);
}

/// <summary>Minimal JSON-RPC client for Piston's stateless streamable-HTTP MCP endpoint.</summary>
public sealed class PistonMcpClient(Uri endpoint) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private int _nextId;

    public async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await SendAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "piston-measurements", ["version"] = "1" },
        }, cancellationToken);

    /// <summary>Calls a tool and returns its text content; throws when the tool reports an error.</summary>
    public async Task<string> CallToolAsync(string name, JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync("tools/call",
            new JsonObject { ["name"] = name, ["arguments"] = arguments ?? new JsonObject() }, cancellationToken);
        var text = string.Concat((result["content"] as JsonArray ?? [])
            .Select(item => item?["text"]?.GetValue<string>() ?? ""));
        if (result["isError"]?.GetValue<bool>() == true)
            throw new InvalidOperationException($"MCP tool {name} failed: {text}");
        return text;
    }

    private async Task<JsonObject> SendAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"MCP {method} returned {(int)response.StatusCode}: {payload}");

        foreach (var message in Messages(payload, response.Content.Headers.ContentType?.MediaType))
        {
            if (message["id"]?.GetValue<int>() != id) continue;
            if (message["error"] is JsonObject error)
                throw new InvalidOperationException($"MCP {method} error: {error.ToJsonString()}");
            return message["result"] as JsonObject ?? throw new InvalidDataException("MCP response has no result");
        }
        throw new InvalidDataException($"MCP {method} response did not contain id {id}");
    }

    private static IEnumerable<JsonObject> Messages(string payload, string? mediaType)
    {
        if (mediaType == "text/event-stream")
        {
            foreach (var line in payload.Split('\n'))
                if (line.StartsWith("data:", StringComparison.Ordinal) && JsonNode.Parse(line[5..].Trim()) is JsonObject data)
                    yield return data;
        }
        else if (JsonNode.Parse(payload) is JsonObject single)
        {
            yield return single;
        }
    }

    public void Dispose() => _http.Dispose();
}
