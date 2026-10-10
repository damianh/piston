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
