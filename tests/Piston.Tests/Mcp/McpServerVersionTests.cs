// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.IO.Pipelines;
using System.Reflection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Piston.Hosting;
using Piston.Mcp;
using Xunit;

namespace Piston.Tests.Mcp;

public sealed class McpServerVersionTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Initialize_AdvertisesAssemblyProductVersionWithoutCommitMetadata()
    {
        var informationalVersion = typeof(McpServiceRegistration).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.NotNull(informationalVersion);
        var productVersion = informationalVersion.Split('+', 2)[0];

        var toServer = new Pipe();
        var toClient = new Pipe();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var host = new McpStdioHost(new McpStdioHostOptions
        {
            StartupError = "No engine is needed for the version handshake.",
            Input = toServer.Reader.AsStream(),
            Output = toClient.Writer.AsStream(),
            MonitorParentProcess = false,
            Log = _ => { },
        });
        var run = host.RunAsync(shutdown.Token);

        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
                cancellationToken: _ct);

            var advertisedVersion = client.ServerInfo.Version;
            Assert.NotNull(advertisedVersion);
            Assert.Equal("piston", client.ServerInfo.Name);
            Assert.Equal(productVersion, advertisedVersion);
            Assert.DoesNotContain("+", advertisedVersion);
        }
        finally
        {
            await shutdown.CancelAsync();
            await toServer.Writer.CompleteAsync();
            _ = await run.WaitAsync(TimeSpan.FromSeconds(15), _ct);
        }
    }
}
