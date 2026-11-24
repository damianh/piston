// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Piston.Engine.Execution;
using Piston.Hosting;
using Xunit;

namespace Piston.Tests.Hosting;

public sealed class DashboardCommandTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task DiscoveryCli_ProducesJsonWithoutStartingAnEngineOrRequiringASolution()
    {
        var root = Directory.CreateTempSubdirectory("piston-discovery-cli-").FullName;
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(root, ".git"));
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Piston.dll"));
            start.ArgumentList.Add("dashboard");
            start.ArgumentList.Add("--json");
            start.Environment["PISTON_EXECUTION_DIRECTORY"] = Path.Combine(root, "coordination");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(_ct);
            var error = process.StandardError.ReadToEndAsync(_ct);
            await process.WaitForExitAsync(_ct).WaitAsync(TimeSpan.FromSeconds(15), _ct);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await error);
            using var document = JsonDocument.Parse(await output);
            Assert.Equal("disconnected", document.RootElement.GetProperty("state").GetString());
            Assert.False(document.RootElement.TryGetProperty("url", out _));
            Assert.False(Directory.Exists(Path.Combine(root, ".piston")));
            Assert.False(Directory.Exists(Path.Combine(root, "coordination")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "--dashboard=false")]
    [InlineData(true, "--dashboard")]
    public async Task LegacySettings_CannotDisableMcpDashboard(bool configured, string? option)
    {
        var root = Directory.CreateTempSubdirectory("piston-dashboard-config-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "App.slnx"), "<Solution />");
            _ = Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".piston.json"), $$"""{"dashboard":{{configured.ToString().ToLowerInvariant()}}}""");
            var coordination = Path.Combine(root, "coordination");
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Piston.dll"));
            start.ArgumentList.Add("mcp");
            if (option is not null)
            {
                start.ArgumentList.Add(option);
            }
            start.Environment["PISTON_EXECUTION_DIRECTORY"] = coordination;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start piston mcp.");
            var error = process.StandardError.ReadToEndAsync(_ct);
            try
            {
                await using var client = await McpClient.CreateAsync(new StreamClientTransport(
                    process.StandardInput.BaseStream, process.StandardOutput.BaseStream), cancellationToken: _ct)
                    .WaitAsync(TimeSpan.FromSeconds(30), _ct);
                var endpoint = DashboardDiscovery.Inspect(root, new ExecutionCoordinationDirectory(coordination));
                Assert.Equal("connected", endpoint.State);
                Assert.StartsWith("http://127.0.0.1:", endpoint.Url);
            }
            finally
            {
                try { process.StandardInput.Close(); } catch (ObjectDisposedException) { } catch (IOException) { }
                try
                {
                    await process.WaitForExitAsync(_ct).WaitAsync(TimeSpan.FromSeconds(30), _ct);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                }
            }
            Assert.Equal(0, process.ExitCode);
            var stderr = await error;
            if (!configured)
            {
                Assert.Contains("dashboard: false", stderr);
                Assert.Contains("always enabled", stderr);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
