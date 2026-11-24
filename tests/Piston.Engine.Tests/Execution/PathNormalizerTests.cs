// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Xunit;

namespace Piston.Engine.Tests.Execution;

public sealed class PathNormalizerTests
{
    [Theory]
    [InlineData(@"\\?\D:\Repo\src\..\..\App.slnx", @"D:\App.slnx")]
    [InlineData(@"\\?\UNC\server\share\Repo\src\..\App.slnx", @"\\server\share\Repo\App.slnx")]
    [InlineData(@"\\?\unc\server\share\Repo\.\App.slnx", @"\\server\share\Repo\App.slnx")]
    public void ExtendedPaths_ResolveDotSegmentsAndPreserveCase(string input, string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(expected, PathNormalizer.GetFullPath(input));
        Assert.Equal(expected, PathNormalizer.GetFullPath(expected));
    }

    [Theory]
    [InlineData(@"\\?\D:\literal.\App.slnx")]
    [InlineData(@"\\?\D:\literal \App.slnx")]
    [InlineData(@"\\?\UNC\server\share\literal.\App.slnx")]
    [InlineData(@"\\?\D:\Repo\NUL.txt")]
    [InlineData(@"\\?\D:\Repo\COM1\App.slnx")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\App.slnx")]
    public void NamespaceDependentPaths_AreNotChanged(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(path, PathNormalizer.GetFullPath(path));
    }

    [Fact]
    public void RelativePaths_UseTheNormalizedBaseDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(@"D:\Repo\App.slnx", PathNormalizer.GetFullPath(@"..\..\App.slnx", @"\\?\D:\Repo\src\nested"));
    }

    [Fact]
    public void LongSdkWorkingDirectory_PreservesSdkSelectionOrReportsTheLimit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var temporary = Directory.CreateTempSubdirectory("piston-sdk-path-").FullName;
        try
        {
            var path = temporary;
            File.WriteAllText(Path.Combine(temporary, "global.json"), """{"sdk":{"version":"11.0.100"}}""");
            while (path.Length < 280)
            {
                path = Path.Combine(path, "nested-directory");
            }
            _ = Directory.CreateDirectory(path);
            var cwd = PathNormalizer.GetSdkWorkingDirectory(@"\\?\" + path);
            Assert.True(cwd.Length < 260);
            Assert.StartsWith(cwd + "\\", path, StringComparison.Ordinal);
            Assert.Equal(cwd, PathNormalizer.GetSdkWorkingDirectory(path));

            File.WriteAllText(Path.Combine(path, "global.json"), """{"sdk":{"version":"11.0.100"}}""");
            var error = Assert.Throws<PathTooLongException>(() => PathNormalizer.GetSdkWorkingDirectory(path));
            Assert.Contains("SDK selection", error.Message);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public void NonWindowsPaths_KeepNativeSemantics()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string path = @"\\?\D:\Repo\App.slnx";
        Assert.Equal(Path.GetFullPath(path), PathNormalizer.GetFullPath(path));
    }
}
