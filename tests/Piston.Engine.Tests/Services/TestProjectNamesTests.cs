// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TestProjectNamesTests
{
    [Theory]
    [InlineData(@"\\?\C:\Repo", @"\\?\C:\Repo\CON.Tests.csproj", "CON.Tests.csproj")]
    [InlineData(@"C:\Repo", @"\\?\C:\Repo\CON.Tests.csproj", "CON.Tests.csproj")]
    [InlineData(@"\\server\share\Repo", @"\\?\UNC\server\share\Repo\CON.Tests.csproj", "CON.Tests.csproj")]
    [InlineData(@"\\?\UNC\server\share\Repo", @"\\?\UNC\server\share\Repo\CON.Tests.csproj", "CON.Tests.csproj")]
    [InlineData(@"\\?\C:\Repo\literal.", @"C:\Repo\Normal.csproj", "../Normal.csproj")]
    [InlineData(@"\\?\C:\Repo", @"\\?\D:\Repo\CON.Tests.csproj", "//?/D:/Repo/CON.Tests.csproj")]
    [InlineData(@"\\?\C:\Repo", @"\\?\C:\Repo\Normal.csproj", "Normal.csproj")]
    public void ProjectId_UsesACommonNamespaceForNamespaceDependentPaths(
        string solutionDirectory, string projectPath, string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(expected, TestProjectNames.ProjectId(solutionDirectory, projectPath));
    }
}
