// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Protocol.Messages;

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

    // ── Run history ──

    /// <summary>History marks of failing and flaky tests, keyed by project and test result key.</summary>
    public IReadOnlyDictionary<(string Project, string Test), TestMarkDto> TestMarks { get; set; } =
        new Dictionary<(string Project, string Test), TestMarkDto>();

    public TestMarkDto? GetTestMark(string? project, string testKey) =>
        project is null ? null : TestMarks.GetValueOrDefault((project, testKey));

    /// <summary>Show only failing tests whose previous result passed (with <see cref="OnlyFlaky"/>, either kind).</summary>
    public bool OnlyNewFailures { get; set; }

    /// <summary>Show only tests that passed and failed on the same source (with <see cref="OnlyNewFailures"/>, either kind).</summary>
    public bool OnlyFlaky { get; set; }

    // ── Grouping mode ──
    public GroupingMode Grouping { get; set; } = GroupingMode.ProjectNamespaceClass;

    /// <summary>Lists tests slowest first instead of by name (within each class, status group or the flat list).</summary>
    public bool SortByDuration { get; set; }

    // ── Expand/collapse ──
    public bool TreeExpanded { get; set; } = true;

    /// <summary>Per-node expansion chosen by the user, keyed by <see cref="TestNode.ExpansionKey"/>.</summary>
    public Dictionary<string, bool> NodeExpansion { get; } = new(StringComparer.Ordinal);

    public bool IsNodeExpanded(string key) =>
        NodeExpansion.TryGetValue(key, out var expanded) ? expanded : TreeExpanded;

    // ── Coverage tab ──
    public CoverageGrouping CoverageGrouping { get; set; } = CoverageGrouping.Module;

    /// <summary>Coverage groups the user collapsed, keyed by grouping and <see cref="CoverageGroup.Key"/>.</summary>
    public HashSet<string> CollapsedCoverageGroups { get; } = new(StringComparer.Ordinal);

    public const double DefaultCoverageSplitRightPercent = 40;

    public DetailPosition CoverageDetailPosition { get; set; } = DetailPosition.Right;
    public double CoverageSplitRightPercent { get; set; } = DefaultCoverageSplitRightPercent;
    public double CoverageSplitBottomPercent { get; set; } = DefaultSplitBottomPercent;

    public double CoverageSplitPercent
    {
        get => CoverageDetailPosition == DetailPosition.Right
            ? CoverageSplitRightPercent
            : CoverageSplitBottomPercent;
        set
        {
            var clamped = Math.Clamp(value, MinSplitPercent, MaxSplitPercent);
            if (CoverageDetailPosition == DetailPosition.Right)
            {
                CoverageSplitRightPercent = clamped;
            }
            else
            {
                CoverageSplitBottomPercent = clamped;
            }
        }
    }

    public void ResetCoverageSplit() =>
        CoverageSplitPercent = CoverageDetailPosition == DetailPosition.Right
            ? DefaultCoverageSplitRightPercent
            : DefaultSplitBottomPercent;

    // ── Pinned tests ──
    public HashSet<string> PinnedTestFqns { get; } = new(StringComparer.Ordinal);

    // ── Filter ──
    public string? Filter { get; set; }

    // ── Selection: a test or another tree node, never both ──
    public string? SelectedTestKey { get; set; }
    public string? SelectedTestProject { get; set; }

    public void RevealTest(TestReference test)
    {
        SelectedNodeKey = null;
        SelectedTestKey = test.Key;
        SelectedTestProject = test.Project;
        RevealSelectedTest = true;
    }

    public TestResultDto? FindSelectedTest(IEnumerable<TestSuiteDto> suites)
    {
        if (SelectedTestKey is not { } key)
        {
            return null;
        }

        var tests = suites.Where(s => SelectedTestProject is null || s.Name == SelectedTestProject)
            .SelectMany(s => s.Tests).ToList();
        return tests.FirstOrDefault(t => t.Key == key)
            ?? tests.FirstOrDefault(t => t.FullyQualifiedName == key
                || key.StartsWith(t.FullyQualifiedName + " [", StringComparison.Ordinal));
    }

    /// <summary>
    /// Set when another view opens <see cref="SelectedTestKey"/>; the Tests tab then expands the test's ancestors and
    /// scrolls it into view once.
    /// </summary>
    public bool RevealSelectedTest { get; set; }

    /// <summary>
    /// The project, framework, namespace, class or group shown in the details pane, by its
    /// <see cref="TestNode.ExpansionKey"/>.
    /// </summary>
    public string? SelectedNodeKey { get; set; }

    // ── Failure navigation ──
    public int CurrentFailureIndex { get; set; } = -1;

    // ── Layout ──
    public const double DefaultSplitRightPercent = 50;
    public const double DefaultSplitBottomPercent = 60;
    public const double MinSplitPercent = 15;
    public const double MaxSplitPercent = 85;

    public DetailPosition DetailPosition { get; set; } = DetailPosition.Right;

    /// <summary>Width of the first pane, as a percentage, when details are docked on the right.</summary>
    public double SplitRightPercent { get; set; } = DefaultSplitRightPercent;

    /// <summary>Height of the first pane, as a percentage, when details are docked at the bottom.</summary>
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

    /// <summary>Applies machine-wide dashboard settings; unset values leave the current state alone.</summary>
    public void ApplySettings(DashboardSettings? settings)
    {
        if (settings?.DetailPane is { } pane)
        {
            DetailPosition = pane == DetailPanePosition.Bottom ? DetailPosition.Bottom : DetailPosition.Right;
        }
    }

    public DashboardSettings ToSettings() =>
        new(DetailPosition == DetailPosition.Bottom ? DetailPanePosition.Bottom : DetailPanePosition.Right);
}

/// <summary>A test result key scoped to its suite name, independent of the host's project path separators.</summary>
public sealed record TestReference(string Project, string Key)
{
    public static TestReference FromProjectPath(string projectPath, string key) =>
        new(Path.GetFileNameWithoutExtension(projectPath.Replace('\\', '/')), key);
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
