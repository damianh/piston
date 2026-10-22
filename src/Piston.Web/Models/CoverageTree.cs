// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>How the Coverage tab groups measured files.</summary>
public enum CoverageGrouping
{
    /// <summary>By the assembly (Cobertura package) that contains the file.</summary>
    Module,

    /// <summary>By directory, as a tree.</summary>
    Folder,

    /// <summary>No grouping.</summary>
    Flat,
}

/// <summary>A group of measured files with the coverage of everything under it.</summary>
public sealed class CoverageGroup
{
    public CoverageGroup(string key, string name)
    {
        Key = key;
        Name = name;
    }

    /// <summary>Unique within one grouping; used to remember whether the group is expanded.</summary>
    public string Key { get; }

    public string Name { get; private set; }

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

public static class CoverageTreeBuilder
{
    /// <summary>
    /// Groups <paramref name="files"/> under a root. Groups and files are ordered by name, or most uncovered
    /// lines first when <paramref name="byUncovered"/> is set. In a folder tree, a directory whose only content
    /// is one subdirectory is merged with it (<c>src/Lib/Core</c>).
    /// </summary>
    public static CoverageGroup Build(IEnumerable<CoverageFileSummaryDto> files, CoverageGrouping grouping, bool byUncovered)
    {
        var root = new CoverageGroup("", "");
        switch (grouping)
        {
            case CoverageGrouping.Module:
                foreach (var module in files.GroupBy(f => f.Module ?? "(unknown)", StringComparer.Ordinal))
                {
                    var group = new CoverageGroup(module.Key, module.Key);
                    group.Files.AddRange(module);
                    root.Groups.Add(group);
                }

                break;

            case CoverageGrouping.Folder:
                foreach (var file in files)
                {
                    var parent = root;
                    var segments = file.RelativePath.Split('/', '\\');
                    for (var i = 0; i < segments.Length - 1; i++)
                    {
                        var key = string.Join('/', segments[..(i + 1)]);
                        var child = parent.Groups.Find(g => g.Key == key);
                        if (child is null)
                        {
                            child = new CoverageGroup(key, segments[i]);
                            parent.Groups.Add(child);
                        }

                        parent = child;
                    }

                    parent.Files.Add(file);
                }

                Compact(root);
                break;

            default:
                root.Files.AddRange(files);
                break;
        }

        root.Total();
        Sort(root, byUncovered);
        return root;
    }

    private static void Compact(CoverageGroup group)
    {
        for (var i = 0; i < group.Groups.Count; i++)
        {
            var child = group.Groups[i];
            while (child.Files.Count == 0 && child.Groups.Count == 1)
            {
                var only = child.Groups[0];
                only.Rename($"{child.Name}/{only.Name}");
                child = only;
            }

            group.Groups[i] = child;
            Compact(child);
        }
    }

    private static void Sort(CoverageGroup group, bool byUncovered)
    {
        if (byUncovered)
        {
            group.Groups.Sort((a, b) => b.LinesUncovered != a.LinesUncovered
                ? b.LinesUncovered.CompareTo(a.LinesUncovered)
                : StringComparer.Ordinal.Compare(a.Name, b.Name));
            group.Files.Sort((a, b) => (b.LinesTotal - b.LinesCovered) != (a.LinesTotal - a.LinesCovered)
                ? (b.LinesTotal - b.LinesCovered).CompareTo(a.LinesTotal - a.LinesCovered)
                : StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        }
        else
        {
            group.Groups.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            group.Files.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        }

        foreach (var child in group.Groups)
        {
            Sort(child, byUncovered);
        }
    }
}
