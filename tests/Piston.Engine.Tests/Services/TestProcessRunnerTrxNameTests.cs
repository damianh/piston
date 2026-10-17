// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TestProcessRunnerTrxNameTests
{
    [Theory]
    [InlineData("/tmp/r/piston-results_net8.0_20261011101112.trx", "net8.0")]
    [InlineData("/tmp/r/piston-results_net10.0-windows_20261011101112.trx", "net10.0-windows")]
    [InlineData("/tmp/r/piston-results_netstandard2.0_20261011101112.trx", "netstandard2.0")]
    [InlineData("/tmp/r/piston-results.trx", null)]
    [InlineData("/tmp/r/other_net8.0_20261011101112.trx", null)]
    public void TargetFrameworkOfTrx_ReadsTheFrameworkFromTheLoggerFileName(string path, string? expected) =>
        Assert.Equal(expected, TestProcessRunner.TargetFrameworkOfTrx(path));

    [Fact]
    public void WorkingDirectoryFor_IsTheProjectDirectory_SoItsGlobalJsonApplies()
    {
        var dir = Directory.CreateTempSubdirectory("piston-wd-").FullName;
        try
        {
            Assert.Equal(dir, TestProcessRunner.WorkingDirectoryFor(Path.Combine(dir, "A.Tests.csproj")));
            Assert.Null(TestProcessRunner.WorkingDirectoryFor(Path.Combine(dir, "missing", "A.Tests.csproj")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
