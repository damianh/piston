using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Piston.Engine;
using Piston.Roslyn;

namespace Piston.Mcp;

public static class McpServiceRegistration
{
    public static IServiceCollection AddPistonMcp(this IServiceCollection services)
    {
        services.AddSingleton<IRoslynWorkspace>(_ => RoslynWorkspaceFactory.Create());
        services.AddSingleton<IMcpCallRecorder>(NullMcpCallRecorder.Instance);

        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly(typeof(McpServiceRegistration).Assembly);

        return services;
    }

    public static IServiceCollection AddPistonMcp(this IServiceCollection services, IRoslynWorkspace workspace)
    {
        services.AddSingleton(workspace);
        services.AddSingleton<IMcpCallRecorder>(NullMcpCallRecorder.Instance);

        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly(typeof(McpServiceRegistration).Assembly);

        return services;
    }

    public static IServiceCollection AddPistonMcp(this IServiceCollection services, IRoslynWorkspace workspace, IMcpCallRecorder recorder)
    {
        services.AddSingleton(workspace);
        services.AddSingleton(recorder);

        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly(typeof(McpServiceRegistration).Assembly);

        return services;
    }
}
