// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.IO.Pipes;
using Piston.Hosting;
using Xunit;

namespace Piston.Tests;

public sealed class DaemonLauncherTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateStartInfo_PreservesSelectingConfigForSolutionInAnotherDirectory(bool coverageEnabled)
    {
        var root = Directory.CreateTempSubdirectory("piston-daemon-config-").FullName;
        try
        {
            var solutionDir = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
            var solutionPath = Path.Combine(solutionDir, "Test.slnx");
            File.WriteAllText(solutionPath, "<Solution />");
            File.WriteAllText(Path.Combine(root, ".piston.json"),
                $$"""{"solution":"nested/Test.slnx","coverageEnabled":{{coverageEnabled.ToString().ToLowerInvariant()}},"mcpPort":5210,"parallelism":3}""");
            File.WriteAllText(Path.Combine(solutionDir, ".piston.json"),
                $$"""{"coverageEnabled":{{(!coverageEnabled).ToString().ToLowerInvariant()}}}""");

            var (selectedPath, _) = HostHelpers.ResolveSolution(null, root);
            var start = DaemonLauncher.CreateStartInfo("Piston", selectedPath, "test-pipe", 5321, root);
            Assert.Contains("--no-stdio", start.ArgumentList);
            Assert.DoesNotContain("--coverage", start.ArgumentList);
            Assert.DoesNotContain("--no-coverage", start.ArgumentList);
            var configIndex = start.ArgumentList.IndexOf("--config-directory");
            Assert.True(configIndex >= 0);
            var (_, daemonConfig) = HostHelpers.ResolveSolution(new FileInfo(start.ArgumentList[1]),
                configurationDirectory: start.ArgumentList[configIndex + 1]);

            Assert.Equal(coverageEnabled, daemonConfig.CoverageEnabled);
            Assert.Equal(5210, daemonConfig.McpPort);
            Assert.Equal(3, daemonConfig.Parallelism);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureRunningAsync_ReusesExistingDaemonWithoutSendingConfiguration()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"piston-existing-{Guid.NewGuid():N}";
        await using var daemon = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connection = daemon.WaitForConnectionAsync(ct);

        await DaemonLauncher.EnsureRunningAsync("App.slnx", pipeName, 5199, Path.GetTempPath(), ct);
        await connection.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // The dashboard probes the live daemon but must not send options or a reconfiguration command.
        Assert.Equal(0, await daemon.ReadAsync(new byte[1], ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
    }

    [Theory]
    [InlineData("/sdk/dotnet", true)]
    [InlineData("C:\\sdk\\dotnet.exe", true)]
    [InlineData("/tools/Piston", false)]
    public void CreateStartInfo_PreservesToolEntryPointAndArguments(string executable, bool usesDotnet)
    {
        // Use platform-native separators for the executable-name check.
        executable = executable.Replace('\\', Path.DirectorySeparatorChar);
        var start = DaemonLauncher.CreateStartInfo(executable, "/solution with spaces/test.slnx", "test-pipe", 5321);
        var expected = new List<string>();
        if (usesDotnet)
        {
            expected.Add(typeof(DaemonLauncher).Assembly.Location);
        }

        expected.AddRange(["daemon", "/solution with spaces/test.slnx", "--no-stdio", "--pipe-name", "test-pipe", "--web-port", "5321"]);

        Assert.Equal(executable, start.FileName);
        Assert.Equal(expected, start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }
}
