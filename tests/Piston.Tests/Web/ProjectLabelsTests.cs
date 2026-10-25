// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class ProjectLabelsTests
{
    [Theory]
    [InlineData("/repo/One/Tests.csproj", "/repo/Two/Tests.csproj", "/repo/App.slnx")]
    [InlineData(@"C:\repo\One\Tests.csproj", @"C:\repo\Two\Tests.csproj", @"C:\repo\App.slnx")]
    [InlineData(@"C:\repo\One\Tests.csproj", "C:/repo/Two/Tests.csproj", "C:/repo/App.slnx")]
    [InlineData(@"\\server\repo\One\Tests.csproj", @"\\server\repo\Two\Tests.csproj", @"\\server\repo\App.slnx")]
    public void DuplicateNames_ShowSolutionRelativePathsRegardlessOfHostSeparators(string first, string second, string solution)
    {
        Assert.Equal("Tests (One/Tests.csproj)", ProjectLabels.For(first, [first, second], solution));
        Assert.Equal("Tests (Two/Tests.csproj)", ProjectLabels.For(second, [first, second], solution));
    }

    [Theory]
    [InlineData("/repo/One/Tests.csproj")]
    [InlineData(@"C:\repo\One\Tests.csproj")]
    public void UniqueName_DoesNotShowThePath(string project) => Assert.Equal("Tests", ProjectLabels.For(project, [project], null));

    [Fact]
    public void SeparatorVariantsOfOneProject_AreNotDuplicateNames() => Assert.Equal("Tests", ProjectLabels.For(@"C:\repo\One\Tests.csproj",
            [@"C:\repo\One\Tests.csproj", "C:/repo/One/Tests.csproj"], @"C:\repo\App.slnx"));

    [Fact]
    public void WithoutASolution_DuplicateNamesShowNormalizedProjectPaths() => Assert.Equal("Tests (C:/repo/One/Tests.csproj)", ProjectLabels.For(@"C:\repo\One\Tests.csproj",
            [@"C:\repo\One\Tests.csproj", @"C:\repo\Two\Tests.csproj"], null));

    [Theory]
    [InlineData(@"c:\REPO\One\Tests.csproj", @"C:\repo\Two\Tests.csproj", @"C:\repo\App.slnx", "One/Tests.csproj")]
    [InlineData(@"D:\One\Tests.csproj", @"C:\repo\Two\Tests.csproj", @"C:\repo\App.slnx", "D:/One/Tests.csproj")]
    [InlineData(@"\\SERVER\Repo\One\Tests.csproj", @"\\server\repo\Two\Tests.csproj", @"\\server\repo\App.slnx", "One/Tests.csproj")]
    [InlineData(@"\\server\other\Tests.csproj", @"\\server\repo\Two\Tests.csproj", @"\\server\repo\App.slnx", "//server/other/Tests.csproj")]
    public void DuplicateLabelsRespectWindowsRootsAndCase(string first, string second, string solution, string expected) =>
        Assert.Equal($"Tests ({expected})", ProjectLabels.For(first, [first, second], solution));

    [Fact]
    public void WindowsCaseVariantsOfOneProject_AreNotDuplicateNames() =>
        Assert.Equal("Tests", ProjectLabels.For(@"C:\repo\One\Tests.csproj",
            [@"C:\repo\One\Tests.csproj", @"c:\REPO\one\Tests.csproj"], @"C:\repo\App.slnx"));
}
