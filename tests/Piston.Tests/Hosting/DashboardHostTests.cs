// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using Piston.Engine.Execution;
using Piston.Hosting;
using Piston.Hosting.Protocol;
using Piston.Tests.Mcp;
using Xunit;

namespace Piston.Tests.Hosting;

public sealed class DashboardHostTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task EphemeralEndpoint_ReportsOwnerLossAndRecovery()
    {
        var owner = new ExecutionOwnerInfo(Environment.ProcessId, "App.slnx", "/repo", DateTimeOffset.UtcNow);
        var current = owner;
        await using var router = new ProtocolRouter(new StubEngine());
        await using var host = new DashboardHost();
        await host.StartAsync(router, "http://127.0.0.1:0", () => current, _ct);

        var endpoint = new Uri(host.Url);
        Assert.True(endpoint.Port > 0);
        using var http = new HttpClient { BaseAddress = endpoint };
        var json = await http.GetStringAsync("/api/dashboard", _ct);
        Assert.Contains("\"worktreePath\":\"/repo\"", json);

        current = null;
        using var unavailable = await http.GetAsync("/api/dashboard", _ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);

        current = owner;
        using var recovered = await http.GetAsync("/api/dashboard", _ct);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task ArtifactEndpoint_ServesOnlyKnownArtifacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"piston-dashboard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.txt");
        await File.WriteAllTextAsync(path, "test artifact", _ct);
        try
        {
            var engine = new StubEngine();
            engine.TestArtifacts[("run-1", 2)] = path;
            await using var router = new ProtocolRouter(engine);
            await using var host = new DashboardHost();
            await host.StartAsync(router, "http://127.0.0.1:0", () => null, _ct);

            using var http = new HttpClient { BaseAddress = new Uri(host.Url) };
            using var response = await http.GetAsync("/artifacts/run-1/2", _ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("test artifact", await response.Content.ReadAsStringAsync(_ct));

            using var missing = await http.GetAsync("/artifacts/run-1/3", _ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
