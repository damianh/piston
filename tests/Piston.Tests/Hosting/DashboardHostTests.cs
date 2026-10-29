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
}
