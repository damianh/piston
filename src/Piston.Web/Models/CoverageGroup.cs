// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>A group of measured files with the coverage of everything under it.</summary>
public sealed class CoverageGroup(string key, string name)
{

    /// <summary>Unique within one grouping; used to remember whether the group is expanded.</summary>
    public string Key { get; } = key;

    public string Name { get; private set; } = name;

    public List<CoverageGroup> Groups { get; } = [];

    public List<CoverageFileSummaryDto> Files { get; } = [];

    public int LinesCovered { get; private set; }

    public int LinesTotal { get; private set; }

    public int FileCount { get; private set; }

    public int LinesUncovered => LinesTotal - LinesCovered;

    internal void Rename(string name) => Name = name;

    internal void Total()
    {
        foreach (var group in Groups)
        {
            group.Total();
        }

        LinesCovered = Groups.Sum(g => g.LinesCovered) + Files.Sum(f => f.LinesCovered);
        LinesTotal = Groups.Sum(g => g.LinesTotal) + Files.Sum(f => f.LinesTotal);
        FileCount = Groups.Sum(g => g.FileCount) + Files.Count;
    }
}
