using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Piston.Engine;

namespace Piston.Mcp;

public static class McpServiceRegistration
{
    public static IServiceCollection AddPistonMcp(this IServiceCollection services) =>
        services.AddPistonMcp(NullMcpCallRecorder.Instance);

    public static IServiceCollection AddPistonMcp(this IServiceCollection services, IMcpCallRecorder recorder)
    {
        services.AddSingleton(recorder);

        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly(typeof(McpServiceRegistration).Assembly);

        return services;
    }
}
