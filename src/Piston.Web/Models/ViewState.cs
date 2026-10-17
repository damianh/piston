// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>
/// Holds UI-only state for the Piston web dashboard.
/// </summary>
public sealed class ViewState
{
    // ── Status filter toggles ──
    public bool ShowPassed { get; set; } = true;
    public bool ShowFailed { get; set; } = true;
    public bool ShowSkipped { get; set; } = true;
    public bool ShowNotRun { get; set; } = true;

    // ── Grouping mode ──
    public GroupingMode Grouping { get; set; } = GroupingMode.ProjectNamespaceClass;

    // ── Expand/collapse ──
    public bool TreeExpanded { get; set; } = true;

    /// <summary>Per-node expansion chosen by the user, keyed by <see cref="TestNode.ExpansionKey"/>.</summary>
    public Dictionary<string, bool> NodeExpansion { get; } = new(StringComparer.Ordinal);

    public bool IsNodeExpanded(string key) =>
        NodeExpansion.TryGetValue(key, out var expanded) ? expanded : TreeExpanded;

    // ── Pinned tests ──
    public HashSet<string> PinnedTestFqns { get; } = new(StringComparer.Ordinal);

    // ── Filter ──
    public string? Filter { get; set; }

    // ── Selected test ──
    public string? SelectedTestKey { get; set; }

    // ── Failure navigation ──
    public int CurrentFailureIndex { get; set; } = -1;

    // ── Layout ──
    public const double DefaultSplitRightPercent = 50;
    public const double DefaultSplitBottomPercent = 60;
    public const double MinSplitPercent = 15;
    public const double MaxSplitPercent = 85;

    public DetailPosition DetailPosition { get; set; } = DetailPosition.Right;

    /// <summary>Width of the test tree, as a percentage, when details are docked on the right.</summary>
    public double SplitRightPercent { get; set; } = DefaultSplitRightPercent;

    /// <summary>Height of the test tree, as a percentage, when details are docked at the bottom.</summary>
    public double SplitBottomPercent { get; set; } = DefaultSplitBottomPercent;

    public double SplitPercent
    {
        get => DetailPosition == DetailPosition.Right ? SplitRightPercent : SplitBottomPercent;
        set
        {
            var clamped = Math.Clamp(value, MinSplitPercent, MaxSplitPercent);
            if (DetailPosition == DetailPosition.Right)
            {
                SplitRightPercent = clamped;
            }
            else
            {
                SplitBottomPercent = clamped;
            }
        }
    }

    public void ResetSplit() =>
        SplitPercent = DetailPosition == DetailPosition.Right ? DefaultSplitRightPercent : DefaultSplitBottomPercent;
}

public enum DetailPosition
{
    Right,
    Bottom,
}

public enum GroupingMode
{
    ProjectNamespaceClass,
    ByStatus,
    Flat,
}
