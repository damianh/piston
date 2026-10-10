using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Piston.Measurements;
using Xunit;

namespace Piston.Measurements.Tests;

[CollectionDefinition("Container environment", DisableParallelization = true)]
public sealed class ContainerEnvironmentCollection;

[Collection("Container environment")]
public sealed class ContainerTests
{
    private static JsonObject Row(string label = "trial") => new()
    {
        ["Id"] = "owned",
        ["State"] = "running",
        ["Labels"] = new JsonObject { ["piston.measurement.trial"] = label }
    };
    private static JsonObject Details(string label = "trial") => new()
    {
        ["Config"] = new JsonObject { ["Labels"] = new JsonObject { ["piston.measurement.trial"] = label } },
        ["Image"] = "sha256:image",
        ["HostConfig"] = new JsonObject { ["Memory"] = 512L * 1024 * 1024, ["NanoCpus"] = 1_000_000_000L }
    };
    private static JsonObject Stats() => new()
    {
        ["cpu_stats"] = new JsonObject { ["cpu_usage"] = new JsonObject { ["total_usage"] = 123L } },
        ["memory_stats"] = new JsonObject { ["usage"] = 456L, ["limit"] = 512L * 1024 * 1024 }
    };

    [Theory]
    [InlineData("")]
    [InlineData("tcp://localhost:2375")]
    [InlineData("unix://remote/socket")]
    [InlineData("unix:///socket?query")]
    public void RequiresLocalSocket(string host) =>
        Assert.Throws<ArgumentException>(() => ContainerApi.ValidateHost(host));

    [Fact]
    public async Task ObservationAndCleanupUseExactLabelAndConfirmedDelete()
    {
        var api = new FakeApi();
        using var observer = new ContainerObserver(api, "trial") { Phase = "edited" };
        await observer.ObserveOnce();
        var sample = Assert.Single(observer.Stats.Samples);
        Assert.Equal("edited", sample.Phase);
        Assert.Equal(123, sample.CpuTotalNs);
        Assert.Equal(512L * 1024 * 1024, observer.Stats.Containers["owned"].MemoryLimitBytes);
        api.Listings = new Queue<JsonObject[]>([[Row()], []]);
        var result = await observer.Finish();
        Assert.True(result.Confirmed);
        Assert.Equal(["owned"], result.RemovedByHarness);
        Assert.Contains(("DELETE", "/containers/owned?force=true&v=true"), api.Calls);
    }

    [Fact]
    public async Task PhaseCrossingSamplesAreDiscardedIncludingReturnToSamePhase()
    {
        foreach (var returnToSamePhase in new[] { false, true })
        {
            var api = new FakeApi();
            using var observer = new ContainerObserver(api, "trial") { Phase = "baseline" };
            api.RequestOverride = (_, path) =>
            {
                if (!path.EndsWith("/stats?stream=false", StringComparison.Ordinal)) return Details();
                observer.Phase = "edited";
                if (returnToSamePhase) observer.Phase = "baseline";
                return Stats();
            };
            await observer.ObserveOnce();
            Assert.Empty(observer.Stats.Samples);
            Assert.Empty(observer.Stats.Errors);
        }
    }

    [Fact]
    public async Task CleanupDoesNotClaimRacing404()
    {
        var api = new FakeApi { Listings = new Queue<JsonObject[]>([[Row()], []]) };
        api.RequestOverride = (method, _) => method == "DELETE" ? null : Details();
        using var observer = new ContainerObserver(api, "trial");
        var result = await observer.Finish();
        Assert.True(result.Confirmed);
        Assert.Empty(result.RemovedByHarness);
    }

    [Fact]
    public async Task CleanupRefusesMismatchedLabelsAndSurvivors()
    {
        foreach (var mismatch in new[] { true, false })
        {
            var api = new FakeApi();
            if (mismatch) api.RequestOverride = (_, _) => Details("other");
            using var observer = new ContainerObserver(api, "trial");
            await Assert.ThrowsAsync<InvalidDataException>(observer.Finish);
            if (mismatch) Assert.DoesNotContain(api.Calls, call => call.Method == "DELETE");
        }
    }

    [Fact]
    public async Task ListingLabelMismatchNeverInspectsOrDeletes()
    {
        var api = new FakeApi { Listings = new Queue<JsonObject[]>([[Row("other")]]) };
        using var observer = new ContainerObserver(api, "trial");
        await observer.ObserveOnce();
        Assert.Single(observer.Stats.Errors);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task MissingStatsAndApiFailuresRemainExplicit()
    {
        var api = new FakeApi
        {
            RequestOverride = (_, path) =>
            path.EndsWith("/stats?stream=false", StringComparison.Ordinal) ? new JsonObject() : Details()
        };
        using var observer = new ContainerObserver(api, "trial");
        await observer.ObserveOnce();
        Assert.Empty(observer.Stats.Samples);
        Assert.Contains("cpu_stats", Assert.Single(observer.Stats.Errors).Error);
        api.RequestOverride = (_, _) => throw new HttpRequestException("socket unavailable");
        await observer.ObserveOnce();
        Assert.Equal(2, observer.Stats.Errors.Count);
    }

    [Theory]
    [InlineData(200, "{}")]
    [InlineData(204, "")]
    [InlineData(404, "")]
    [InlineData(500, "{}")]
    public async Task UnixApiPreservesResponseStatus(int status, string body)
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "api.sock");
        using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(path));
        server.Listen(1);
        var previous = Environment.GetEnvironmentVariable("DOCKER_HOST");
        try
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", "unix://" + path);
            var serving = Serve(server, status, body);
            using var api = new ContainerApi();
            if (status == 500)
                await Assert.ThrowsAsync<HttpRequestException>(() => api.Request("DELETE", "/containers/owned?force=true&v=true"));
            else
            {
                var response = await api.Request("DELETE", "/containers/owned?force=true&v=true");
                Assert.Equal(status == 404, response is null);
            }
            Assert.StartsWith("DELETE /v1.41/containers/owned?force=true&v=true", await serving);
        }
        finally { Environment.SetEnvironmentVariable("DOCKER_HOST", previous); }
    }

    private static async Task<string> Serve(Socket server, int status, string body)
    {
        using var socket = await server.AcceptAsync();
        await using var stream = new NetworkStream(socket);
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var first = await reader.ReadLineAsync() ?? throw new InvalidDataException("Missing request");
        while (await reader.ReadLineAsync() is { Length: > 0 }) { }
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Result\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
        await stream.WriteAsync(bytes);
        return first;
    }

    private sealed class FakeApi : IContainerApi
    {
        public Queue<JsonObject[]>? Listings { get; set; }
        public Func<string, string, JsonNode?>? RequestOverride { get; set; }
        public List<(string Method, string Path)> Calls { get; } = [];
        public Task<JsonObject[]> Containers(string trialId) => Task.FromResult(
            Listings is { Count: > 0 } ? Listings.Dequeue() : [Row()]);
        public Task<JsonNode?> Request(string method, string path)
        {
            Calls.Add((method, path));
            return Task.FromResult(RequestOverride is not null ? RequestOverride(method, path) :
                path.EndsWith("/stats?stream=false", StringComparison.Ordinal) ? Stats() :
                method == "DELETE" ? new JsonObject() : Details());
        }
    }
}
