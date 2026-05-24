using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Piston.Roslyn;

namespace Piston.Mcp;

public static class McpServiceRegistration
{
    public static IServiceCollection AddPistonMcp(this IServiceCollection services)
    {
        services.AddSingleton<IRoslynWorkspace>(_ => RoslynWorkspaceFactory.Create());

        services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(McpServiceRegistration).Assembly);

        return services;
    }

    public static IServiceCollection AddPistonMcp(this IServiceCollection services, IRoslynWorkspace workspace)
    {
        services.AddSingleton(workspace);

        services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(McpServiceRegistration).Assembly);

        return services;
    }
}
