using Piston.Cli;
using Piston.Hosting;
using Xunit;

namespace Piston.Protocol.Tests;

public sealed class DaemonLauncherTests
{
    [Fact]
    public void CreateStartInfo_PreservesSelectingConfigForSolutionInAnotherDirectory()
    {
        var root = Directory.CreateTempSubdirectory("piston-daemon-config-").FullName;
        try
        {
            var solutionDir = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
            var solutionPath = Path.Combine(solutionDir, "Test.slnx");
            File.WriteAllText(solutionPath, "<Solution />");
            File.WriteAllText(Path.Combine(root, ".piston.json"),
                """{"solution":"nested/Test.slnx","coverageEnabled":true,"mcpPort":5210,"parallelism":3}""");
            File.WriteAllText(Path.Combine(solutionDir, ".piston.json"), """{"coverageEnabled":false}""");

            var (selectedPath, _) = HostHelpers.ResolveSolution(null, root);
            var start = DaemonLauncher.CreateStartInfo("Piston", selectedPath, "test-pipe", 5321, root);
            var configIndex = start.ArgumentList.IndexOf("--config-directory");
            Assert.True(configIndex >= 0);
            var (_, daemonConfig) = HostHelpers.ResolveSolution(new FileInfo(start.ArgumentList[1]),
                configurationDirectory: start.ArgumentList[configIndex + 1]);

            Assert.True(daemonConfig.CoverageEnabled);
            Assert.Equal(5210, daemonConfig.McpPort);
            Assert.Equal(3, daemonConfig.Parallelism);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
            expected.Add(typeof(DaemonLauncher).Assembly.Location);
        expected.AddRange(["daemon", "/solution with spaces/test.slnx", "--pipe-name", "test-pipe", "--web-port", "5321"]);

        Assert.Equal(executable, start.FileName);
        Assert.Equal(expected, start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }
}
