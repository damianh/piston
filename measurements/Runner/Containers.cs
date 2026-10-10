using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Piston.Measurements;

public interface IContainerApi
{
    Task<JsonNode?> Request(string method, string path);
    Task<JsonObject[]> Containers(string trialId);
}

public sealed class ContainerApi : IContainerApi, IDisposable
{
    private readonly HttpClient client;

    public static string ValidateHost(string? host)
    {
        if (host is null || !Uri.TryCreate(host, UriKind.Absolute, out var uri) ||
            uri.Scheme != "unix" || uri.Host.Length != 0 || uri.AbsolutePath.Length <= 1 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Container telemetry requires a local unix:// DOCKER_HOST");
        return Uri.UnescapeDataString(uri.AbsolutePath);
    }

    public ContainerApi()
    {
        var path = ValidateHost(Environment.GetEnvironmentVariable("DOCKER_HOST"));
        client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        })
        {
            BaseAddress = new Uri("http://localhost/v1.41/"),
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    public async Task<JsonNode?> Request(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path.TrimStart('/'));
        using var response = await client.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return body.Length == 0 ? new JsonObject() :
            JsonNode.Parse(body) ?? throw new InvalidDataException("Empty container API JSON");
    }

    public async Task<JsonObject[]> Containers(string trialId)
    {
        var filters = Uri.EscapeDataString(JsonSerializer.Serialize(
            new { label = new[] { $"piston.measurement.trial={trialId}" } }));
        var response = await Request("GET", "/containers/json?all=true&filters=" + filters);
        if (response is not JsonArray array) throw new InvalidDataException("Container listing did not return a list");
        var rows = array.Select(node => node as JsonObject ??
            throw new InvalidDataException("Invalid container row")).ToArray();
        if (rows.Any(row => Label(row["Labels"]) != trialId))
            throw new InvalidDataException("Container API returned an unrelated container");
        return rows;
    }

    internal static string? Label(JsonNode? labels) => labels?["piston.measurement.trial"]?.GetValue<string>();
    public void Dispose() => client.Dispose();
}

public sealed record ContainerSample(string ContainerId, string Phase, long MonotonicNs,
    long CpuTotalNs, long MemoryUsageBytes, long MemoryLimitBytes);
public sealed record ContainerDetails(string Image, long MemoryLimitBytes, long NanoCpus);
public sealed record TelemetryError(string Phase, string Error);
public sealed record ContainerStats(Dictionary<string, ContainerDetails> Containers,
    List<ContainerSample> Samples, List<TelemetryError> Errors,
    int PollIntervalMs = 250, int RequestTimeoutSeconds = 3);
public sealed record ContainerCleanup(bool Confirmed, string[] Remaining, string[] RemovedByHarness,
    string[] ObservedContainerIds, string Scope);

public sealed class ContainerObserver(IContainerApi api, string trialId) : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly object phaseLock = new();
    private string phase = "setup";
    private long generation;
    private Task? observation;
    public ContainerStats Stats { get; } = new([], [], []);
    public string Phase
    {
        get { lock (phaseLock) return phase; }
        set { lock (phaseLock) { phase = value; generation++; } }
    }

    public void Start() => observation = Observe();

    public async Task ObserveOnce()
    {
        try
        {
            foreach (var row in await api.Containers(trialId))
            {
                if (ContainerApi.Label(row["Labels"]) != trialId)
                    throw new InvalidDataException("Container API returned an unrelated container");
                var id = String(row, "Id");
                if (!Stats.Containers.ContainsKey(id))
                {
                    var details = await api.Request("GET", $"/containers/{id}/json");
                    if (details is null) continue;
                    if (ContainerApi.Label(details["Config"]?["Labels"]) != trialId)
                        throw new InvalidDataException("Inspection label did not match trial");
                    var config = details["HostConfig"] ?? throw new InvalidDataException("Missing HostConfig");
                    Stats.Containers[id] = new(String(details, "Image"), Number(config, "Memory"),
                        Number(config, "NanoCpus"));
                }
                if (String(row, "State") != "running") continue;
                string sampledPhase;
                long sampledGeneration;
                lock (phaseLock) { sampledPhase = phase; sampledGeneration = generation; }
                var stats = await api.Request("GET", $"/containers/{id}/stats?stream=false");
                lock (phaseLock)
                {
                    if (stats is null || sampledGeneration != generation) continue;
                    var cpu = stats["cpu_stats"]?["cpu_usage"] ??
                        throw new InvalidDataException("Missing cpu_stats");
                    var memory = stats["memory_stats"] ?? throw new InvalidDataException("Missing memory_stats");
                    Stats.Samples.Add(new(id, sampledPhase, Evidence.NowNs,
                        Number(cpu, "total_usage"), Number(memory, "usage"), Number(memory, "limit")));
                }
            }
        }
        catch (Exception error) when (IsTelemetryError(error))
        {
            Stats.Errors.Add(new(Phase, error.Message));
        }
    }

    private async Task Observe()
    {
        while (!stop.IsCancellationRequested)
        {
            await ObserveOnce();
            try { await Task.Delay(250, stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        }
    }

    public async Task Stop()
    {
        await stop.CancelAsync();
        if (observation is not null) await observation;
    }

    public async Task<ContainerCleanup> Finish()
    {
        await Stop();
        var removed = new List<string>();
        foreach (var row in await api.Containers(trialId))
        {
            if (ContainerApi.Label(row["Labels"]) != trialId)
                throw new InvalidDataException("Refusing cleanup: listing label mismatch");
            var id = String(row, "Id");
            var details = await api.Request("GET", $"/containers/{id}/json");
            if (details is null) continue;
            if (ContainerApi.Label(details["Config"]?["Labels"]) != trialId)
                throw new InvalidDataException("Refusing cleanup: inspection label mismatch");
            if (await api.Request("DELETE", $"/containers/{id}?force=true&v=true") is not null)
                removed.Add(id);
        }
        if ((await api.Containers(trialId)).Length > 0)
            throw new InvalidDataException("Trial-labeled containers remain after cleanup");
        return new(true, [], removed.ToArray(), Stats.Containers.Keys.Order(StringComparer.Ordinal).ToArray(),
            $"piston.measurement.trial={trialId}");
    }

    internal static bool IsTelemetryError(Exception error) =>
        error is InvalidDataException or IOException or HttpRequestException or OperationCanceledException or
            JsonException or InvalidOperationException or KeyNotFoundException or FormatException;
    private static string String(JsonNode node, string name) =>
        node[name]?.GetValue<string>() ?? throw new InvalidDataException("Missing container field: " + name);
    private static long Number(JsonNode node, string name) =>
        node[name]?.GetValue<long>() ?? throw new InvalidDataException("Missing container field: " + name);
    public void Dispose() => stop.Dispose();
}
