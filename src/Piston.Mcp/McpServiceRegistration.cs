// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Piston.Engine;

namespace Piston.Mcp;

public static class McpServiceRegistration
{
    public static IServiceCollection AddPistonMcp(this IServiceCollection services, IMcpCallRecorder? recorder = null)
    {
        services.AddSingleton<IMcpCallRecorder>(recorder ?? NullMcpCallRecorder.Instance);

        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly(typeof(McpServiceRegistration).Assembly);

        return services;
    }
}
