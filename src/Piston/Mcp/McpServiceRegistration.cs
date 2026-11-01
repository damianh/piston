// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Piston.Engine;

namespace Piston.Mcp;

public static class McpServiceRegistration
{
    /// <summary>Registers the Piston MCP tools over the stateless HTTP transport (daemon mode).</summary>
    public static IServiceCollection AddPistonMcp(this IServiceCollection services, IMcpCallRecorder? recorder = null)
    {
        _ = services.AddPistonMcpTools(recorder).WithHttpTransport(options => options.Stateless = true);
        return services;
    }

    /// <summary>
    /// Registers the Piston MCP tools without a transport. <paramref name="availability"/> gates every tool;
    /// when null the tools are always available.
    /// </summary>
    public static IMcpServerBuilder AddPistonMcpTools(
        this IServiceCollection services, IMcpCallRecorder? recorder = null, EngineAvailability? availability = null)
    {
        _ = services.AddSingleton<IMcpCallRecorder>(recorder ?? NullMcpCallRecorder.Instance);
        _ = services.AddSingleton(availability ?? new EngineAvailability());
        var informationalVersion = typeof(McpServiceRegistration).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("Piston assembly informational version is unavailable.");

        return services.AddMcpServer(options =>
            {
                options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "piston",
                    Version = informationalVersion.Split('+', 2)[0],
                };
            })
            .WithToolsFromAssembly(typeof(McpServiceRegistration).Assembly);
    }
}
