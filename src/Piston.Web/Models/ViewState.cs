namespace Piston.Web.Models;

/// <summary>
/// Holds UI-only state for the Piston web dashboard.
/// </summary>
public sealed class ViewState
{
    // ── Status filter toggles ──
    public bool ShowPassed  { get; set; } = true;
    public bool ShowFailed  { get; set; } = true;
    public bool ShowSkipped { get; set; } = true;
    public bool ShowNotRun  { get; set; } = true;

    // ── Grouping mode ──
    public GroupingMode Grouping { get; set; } = GroupingMode.ProjectNamespaceClass;

    // ── Expand/collapse ──
    public bool TreeExpanded { get; set; } = true;

    // ── Pinned tests ──
    public HashSet<string> PinnedTestFqns { get; } = new(StringComparer.Ordinal);

    // ── Selected test ──
    public string? SelectedTestFqn { get; set; }

    // ── Failure navigation ──
    public int CurrentFailureIndex { get; set; } = -1;
}

public enum GroupingMode
{
    ProjectNamespaceClass,
    ByStatus,
    Flat,
}
