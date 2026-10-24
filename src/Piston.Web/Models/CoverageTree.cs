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

/// <summary>The column the coverage file list is ordered by.</summary>
public enum CoverageSort
{
    /// <summary>By group name and file path.</summary>
    Name,

    /// <summary>By line coverage; ascending puts the least covered first.</summary>
    Coverage,
}

public static class CoverageTreeBuilder
{
    /// <summary>
    /// Groups <paramref name="files"/> under a root, ordering groups and files by <paramref name="sort"/>.
    /// Coverage ties put more uncovered lines first, then fall back to name. In a folder tree, a directory whose
    /// only content is one subdirectory is merged with it (<c>src/Lib/Core</c>).
    /// </summary>
    public static CoverageGroup Build(
        IEnumerable<CoverageFileSummaryDto> files,
        CoverageGrouping grouping,
        CoverageSort sort = CoverageSort.Name,
        bool descending = false)
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
        Sort(root, sort, descending);
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

    private static void Sort(CoverageGroup group, CoverageSort sort, bool descending)
    {
        var direction = descending ? -1 : 1;
        if (sort == CoverageSort.Coverage)
        {
            group.Groups.Sort((a, b) => ByCoverage(a.LinesCovered, a.LinesTotal, b.LinesCovered, b.LinesTotal, direction)
                is var c and not 0 ? c : StringComparer.Ordinal.Compare(a.Name, b.Name));
            group.Files.Sort((a, b) => ByCoverage(a.LinesCovered, a.LinesTotal, b.LinesCovered, b.LinesTotal, direction)
                is var c and not 0 ? c : StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        }
        else
        {
            group.Groups.Sort((a, b) => direction * StringComparer.Ordinal.Compare(a.Name, b.Name));
            group.Files.Sort((a, b) => direction * StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        }

        foreach (var child in group.Groups)
        {
            Sort(child, sort, descending);
        }
    }

    // A file with no coverable lines counts as fully covered.
    private static int ByCoverage(int aCovered, int aTotal, int bCovered, int bTotal, int direction)
    {
        var aRatio = aTotal == 0 ? 1d : (double)aCovered / aTotal;
        var bRatio = bTotal == 0 ? 1d : (double)bCovered / bTotal;
        return aRatio != bRatio
            ? direction * aRatio.CompareTo(bRatio)
            : direction * (bTotal - bCovered).CompareTo(aTotal - aCovered);
    }
}
