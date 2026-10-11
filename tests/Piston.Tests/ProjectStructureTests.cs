// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine;
using Piston.Engine.Models;
using Piston.Hosting;
using Piston.Mcp;
using Piston.Mcp.Tools;
using Piston.Protocol.JsonRpc;
using Xunit;

namespace Piston.Tests;

public sealed class ProjectStructureTests
{
    [Fact]
    public void HostAndMcp_ArePartOfTheToolAssembly()
    {
        var assembly = typeof(DaemonLauncher).Assembly;

        Assert.Equal("Piston", assembly.GetName().Name);
        Assert.Same(assembly, typeof(DaemonHost).Assembly);
        Assert.Same(assembly, typeof(McpServiceRegistration).Assembly);
        Assert.Same(assembly, typeof(TestTools).Assembly);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(),
            a => a.Name is "Piston.Hosting" or "Piston.Mcp" or "Piston.Desktop");
    }

    [Fact]
    public void Engine_OwnsItsActivityContracts_WithoutWireDependencies()
    {
        var assembly = typeof(PistonEngine).Assembly;

        Assert.Same(assembly, typeof(EngineActivityEvent).Assembly);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(),
            a => a.Name is "Piston" or "Piston.Protocol" or "Piston.Hosting" or "Piston.Mcp");
        Assert.DoesNotContain(typeof(JsonRpcSerializer).Assembly.GetReferencedAssemblies(),
            a => a.Name is "Piston" or "Piston.Engine");
    }
}
