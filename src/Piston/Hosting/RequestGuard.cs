// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Piston.Hosting;

/// <summary>
/// Protects the daemon's HTTP endpoints (dashboard, <c>/ws</c> and MCP) from web pages. The endpoints listen
/// on <c>localhost</c> only, but any page open in a browser on the same machine can still reach them:
/// <list type="bullet">
/// <item>DNS rebinding: a hostile name that resolves to 127.0.0.1 makes the page same-origin with Piston. The
/// <c>Host</c> header then names the hostile host, so only local host names are accepted (400 otherwise).</item>
/// <item>Cross-site requests: a page may open a WebSocket or POST to <c>localhost</c> (WebSockets are not
/// subject to CORS). Browsers send <c>Origin</c> with those, so a request whose <c>Origin</c> is not the
/// requested host and port is refused (403). Clients without <c>Origin</c> (the CLI, MCP clients) are allowed
/// unless the browser marks the request <c>Sec-Fetch-Site: cross-site</c>.</item>
/// </list>
/// See SECURITY.md.
/// </summary>
internal static class RequestGuard
{
    /// <summary>Extra host names to accept, separated by <c>;</c> or <c>,</c> (e.g. a reverse proxy's name).</summary>
    public const string AllowedHostsVariable = "PISTON_ALLOWED_HOSTS";

    private static readonly string[] DefaultHosts = ["localhost", "127.0.0.1", "[::1]", "::1"];

    /// <summary>The configured extra host names of this process.</summary>
    public static IReadOnlyList<string> ConfiguredHosts { get; } =
        ParseHosts(Environment.GetEnvironmentVariable(AllowedHostsVariable));

    /// <summary>Adds the guard, and the dashboard's response headers when <paramref name="securityHeaders"/> is set.</summary>
    public static IApplicationBuilder UseRequestGuard(this IApplicationBuilder app, bool securityHeaders)
    {
        var extraHosts = ConfiguredHosts;
        return app.Use(async (context, next) =>
        {
            if (Check(context.Request, extraHosts) is { } rejection)
            {
                context.Response.StatusCode = rejection.StatusCode;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(rejection.Reason, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            if (securityHeaders)
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Cross-Origin-Resource-Policy"] = "same-origin";
            }

            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>Why <paramref name="request"/> is refused, or <see langword="null"/> when it is allowed.</summary>
    public static Rejection? Check(HttpRequest request, IReadOnlyList<string> extraHosts)
    {
        var host = request.Host;
        if (!host.HasValue || !IsAllowedHost(host.Host, extraHosts))
        {
            return new(StatusCodes.Status400BadRequest,
                $"Host '{host.Value}' is not allowed. Piston accepts local host names only; " +
                $"add others to {AllowedHostsVariable}.");
        }

        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0)
        {
            return IsSameOrigin(origin, request)
                ? null
                : new(StatusCodes.Status403Forbidden, $"Cross-origin request from '{origin}' refused.");
        }

        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        var changesState = request.HttpContext.WebSockets.IsWebSocketRequest || !IsSafeMethod(request.Method);
        return changesState && fetchSite.Equals("cross-site", StringComparison.OrdinalIgnoreCase)
            ? new(StatusCodes.Status403Forbidden, "Cross-site request refused.")
            : null;
    }

    internal static IReadOnlyList<string> ParseHosts(string? value) =>
        (value ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // "Any host" would switch off the DNS rebinding protection; name the hosts instead.
            .Where(host => host != "*")
            .ToArray();

    internal static bool IsAllowedHost(string host, IReadOnlyList<string> extraHosts)
    {
        if (DefaultHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || extraHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsSameOrigin(string origin, HttpRequest request)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var requestPort = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        return uri.Port == requestPort
            && string.Equals(uri.IdnHost.Trim('[', ']'), request.Host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    internal sealed record Rejection(int StatusCode, string Reason);
}
