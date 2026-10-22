// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class CoverageTreeTests
{
    private static CoverageFileSummaryDto File(string path, string? module, int covered, int total) =>
        new($"/repo/{path}", path, module, total, covered, 0, 0, false, false, DateTimeOffset.UnixEpoch);

    private static readonly CoverageFileSummaryDto[] Files =
    [
        File("src/Lib/Core/B.cs", "Lib", 5, 10),
        File("src/Lib/Core/A.cs", "Lib", 10, 10),
        File("src/Lib/Io/C.cs", "Lib", 0, 20),
        File("src/App/Program.cs", "App", 3, 4),
        File("README.cs", null, 1, 1),
    ];

    [Fact]
    public void Build_ByModule_GroupsFilesAndTotalsTheirLines()
    {
        var root = CoverageTreeBuilder.Build(Files, CoverageGrouping.Module, byUncovered: false);

        Assert.Equal(["(unknown)", "App", "Lib"], root.Groups.Select(g => g.Name));
        var lib = root.Groups[2];
        Assert.Equal(["src/Lib/Core/A.cs", "src/Lib/Core/B.cs", "src/Lib/Io/C.cs"], lib.Files.Select(f => f.RelativePath));
        Assert.Equal((15, 40, 3), (lib.LinesCovered, lib.LinesTotal, lib.FileCount));
        Assert.Equal((19, 45, 5), (root.LinesCovered, root.LinesTotal, root.FileCount));
    }

    [Fact]
    public void Build_ByFolder_NestsDirectoriesAndMergesSingleChildChains()
    {
        var root = CoverageTreeBuilder.Build(Files, CoverageGrouping.Folder, byUncovered: false);

        var src = Assert.Single(root.Groups);
        Assert.Equal("src", src.Name);
        Assert.Equal(["README.cs"], root.Files.Select(f => f.RelativePath));
        Assert.Equal(["App", "Lib"], src.Groups.Select(g => g.Name));
        var lib = src.Groups[1];
        Assert.Equal(["Core", "Io"], lib.Groups.Select(g => g.Name));
        Assert.Equal("src/Lib/Core", lib.Groups[0].Key);
        Assert.Equal((15, 20, 2), (lib.Groups[0].LinesCovered, lib.Groups[0].LinesTotal, lib.Groups[0].FileCount));
    }

    [Fact]
    public void Build_ByFolder_MergedChainKeepsTheDeepestKey()
    {
        var root = CoverageTreeBuilder.Build([File("src/Only/Deep/X.cs", "M", 1, 2)], CoverageGrouping.Folder, byUncovered: false);

        var group = Assert.Single(root.Groups);
        Assert.Equal("src/Only/Deep", group.Name);
        Assert.Equal("src/Only/Deep", group.Key);
        _ = Assert.Single(group.Files);
    }

    [Fact]
    public void Build_ByUncovered_OrdersGroupsAndFilesByMissedLines()
    {
        var root = CoverageTreeBuilder.Build(Files, CoverageGrouping.Module, byUncovered: true);

        Assert.Equal(["Lib", "App", "(unknown)"], root.Groups.Select(g => g.Name));
        Assert.Equal(["src/Lib/Io/C.cs", "src/Lib/Core/B.cs", "src/Lib/Core/A.cs"], root.Groups[0].Files.Select(f => f.RelativePath));
    }

    [Fact]
    public void Build_Flat_ListsEveryFileAtTheRoot()
    {
        var root = CoverageTreeBuilder.Build(Files, CoverageGrouping.Flat, byUncovered: false);

        Assert.Empty(root.Groups);
        Assert.Equal(5, root.Files.Count);
        Assert.Equal("README.cs", root.Files[0].RelativePath);
    }
}
