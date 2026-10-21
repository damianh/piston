// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Piston.Hosting;
using Xunit;

namespace Piston.Tests.Hosting;

public sealed class RequestGuardTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("localhost:5199")]
    [InlineData("LOCALHOST")]
    [InlineData("piston.localhost:5199")]
    [InlineData("127.0.0.1:5199")]
    [InlineData("127.0.0.2")]
    [InlineData("[::1]:5199")]
    public void LocalHosts_AreAllowed(string host)
    {
        Assert.Null(RequestGuard.Check(Request("GET", host), []));
    }

    [Theory]
    [InlineData("evil.example:5199")]
    [InlineData("192.168.1.10:5199")]
    [InlineData("localhost.evil.example")]
    [InlineData("")]
    public void OtherHosts_AreRejected(string host)
    {
        Assert.Equal(StatusCodes.Status400BadRequest, RequestGuard.Check(Request("GET", host), [])?.StatusCode);
    }

    [Fact]
    public void ConfiguredHosts_AreAllowed()
    {
        var extra = RequestGuard.ParseHosts("piston.internal; *");

        Assert.Equal(["piston.internal"], extra);
        Assert.Null(RequestGuard.Check(Request("GET", "piston.internal:5199"), extra));
    }

    [Theory]
    [InlineData("GET", "http://localhost:5199")]
    [InlineData("POST", "http://localhost:5199")]
    [InlineData("POST", null)]
    public void SameOriginAndOriginlessRequests_AreAllowed(string method, string? origin)
    {
        Assert.Null(RequestGuard.Check(Request(method, "localhost:5199", origin), []));
    }

    [Theory]
    [InlineData("GET", "https://evil.example")]
    [InlineData("POST", "https://evil.example")]
    [InlineData("POST", "http://localhost:3000")]
    [InlineData("POST", "http://127.0.0.1:5199")]
    [InlineData("POST", "null")]
    [InlineData("POST", "file://")]
    public void CrossOriginRequests_AreRejected(string method, string origin)
    {
        Assert.Equal(StatusCodes.Status403Forbidden, RequestGuard.Check(Request(method, "localhost:5199", origin), [])?.StatusCode);
    }

    [Fact]
    public void CrossSiteRequestsWithoutOrigin_AreRejectedUnlessSafe()
    {
        var post = Request("POST", "localhost:5199");
        post.Headers["Sec-Fetch-Site"] = "cross-site";
        var get = Request("GET", "localhost:5199");
        get.Headers["Sec-Fetch-Site"] = "cross-site";

        Assert.Equal(StatusCodes.Status403Forbidden, RequestGuard.Check(post, [])?.StatusCode);
        Assert.Null(RequestGuard.Check(get, []));
    }

    [Fact]
    public async Task WebSocketFromAnotherOrigin_IsRefusedBeforeTheHandshake()
    {
        var (app, address) = await StartAppAsync();
        await using var _ = app;
        var uri = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws");

        using var foreign = new ClientWebSocket();
        foreign.Options.SetRequestHeader("Origin", "https://evil.example");
        var ex = await Assert.ThrowsAsync<WebSocketException>(() => foreign.ConnectAsync(uri, _ct));
        Assert.Contains("403", ex.Message, StringComparison.Ordinal);

        using var local = new ClientWebSocket();
        local.Options.SetRequestHeader("Origin", address);
        await local.ConnectAsync(uri, _ct);
    }

    [Fact]
    public async Task ForeignHostHeader_IsRejectedAndSecurityHeadersAreSet()
    {
        var (app, address) = await StartAppAsync();
        await using var _ = app;
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        using var rebound = new HttpRequestMessage(HttpMethod.Get, "/");
        rebound.Headers.Host = "evil.example";
        using var rejected = await client.SendAsync(rebound, _ct);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        using var allowed = await client.GetAsync("/", _ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("nosniff", allowed.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", allowed.Headers.GetValues("Referrer-Policy").Single());
    }

    private static HttpRequest Request(string method, string host, string? origin = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Host = new HostString(host);
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        return context.Request;
    }

    private async Task<(WebApplication App, string Address)> StartAppAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        _ = builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        _ = app.UseRequestGuard(securityHeaders: true);
        _ = app.UseWebSockets();
        _ = app.MapGet("/", () => "ok");
        _ = app.Map("/ws", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
        });
        await app.StartAsync(_ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return (app, address);
    }
}
