// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Piston.Engine;

namespace Piston.Mcp;

public static class McpServiceRegistration
{
    /// <summary>What the server tells a client's model about using it, for clients without the Piston skill.</summary>
    internal const string ServerInstructions =
        "Piston builds and tests this worktree's .NET solution continuously: a full run when it starts, then a run " +
        "of the affected tests after every edit. Use its tools instead of running dotnet build or dotnet test. " +
        "After editing, call run_affected_tests for the result of the tests the change affects. Before reporting " +
        "work as done, call verify: only 'Verdict: Passed' means the present source builds and every test passes. " +
        "A tool error is not a pass: report it, and ask before running another test command instead.";

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
                options.ServerInstructions = ServerInstructions;
            })
            .WithToolsFromAssembly(typeof(McpServiceRegistration).Assembly);
    }
}
