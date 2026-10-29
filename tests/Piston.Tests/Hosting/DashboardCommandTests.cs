// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.Json;
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
    [InlineData(true)]
    [InlineData(false)]
    public void DashboardConfig_IsReadWithTheChosenSolution(bool enabled)
    {
        var root = Directory.CreateTempSubdirectory("piston-dashboard-config-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "App.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(root, ".piston.json"), $$"""{"dashboard":{{enabled.ToString().ToLowerInvariant()}}}""");
            var (_, config) = Piston.Hosting.HostHelpers.ResolveSolution(null, workingDirectory: root);
            Assert.Equal(enabled, config.Dashboard);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
