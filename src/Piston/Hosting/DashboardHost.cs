// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Piston.Engine.Execution;
using Piston.Hosting.Protocol;

namespace Piston.Hosting;

/// <summary>Shared static dashboard and WebSocket endpoint; does not start or control an engine.</summary>
internal sealed class DashboardHost : IAsyncDisposable
{
    private WebApplication? _app;

    public string Url { get; private set; } = "";

    public async Task StartAsync(ProtocolRouter router, string url, Func<ExecutionOwnerInfo?> owner, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "Piston",
            ContentRootPath = AppContext.BaseDirectory,
        });
        // The MCP host reserves stdout for its transport.
        _ = builder.Logging.ClearProviders();
        _ = builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        _app = builder.Build();
        _app.Urls.Add(url);
        _ = _app.UseWebSockets();
        _ = _app.UseRequestGuard(true,
            ContentSecurityPolicy.Build(ContentSecurityPolicy.ReadIndex(_app.Environment.WebRootFileProvider)));
        _ = _app.UseBlazorFrameworkFiles();
        _ = _app.UseDefaultFiles();
        _ = _app.UseStaticFiles();
        _ = _app.UseRouting();
        _ = _app.MapGet("/api/dashboard", () => Results.Json(owner(), DashboardJsonContext.Default.ExecutionOwnerInfo));
        var stopping = _app.Lifetime.ApplicationStopping;
        _ = _app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping);
            await router.AddWebSocketSessionAsync(socket, session.Token).ConfigureAwait(false);
        });
        _ = _app.MapFallbackToFile("index.html");
        await _app.StartAsync(ct).ConfigureAwait(false);
        Url = _app.Urls.Single();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }
}
