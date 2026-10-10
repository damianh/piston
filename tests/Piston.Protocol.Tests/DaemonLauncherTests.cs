using Piston.Cli;
using Xunit;

namespace Piston.Protocol.Tests;

public sealed class DaemonLauncherTests
{
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
