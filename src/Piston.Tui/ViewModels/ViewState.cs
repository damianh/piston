namespace Piston.Tui.ViewModels;

/// <summary>
/// Holds UI-only state that belongs in the view layer.
/// Instantiated by <see cref="Piston.Tui.Views.PistonWindow"/> and mutated by key handlers.
/// </summary>
public sealed class ViewState
{
    // ── Status filter toggles (keys 1-4) ──

    public bool ShowPassed  { get; set; } = true;
    public bool ShowFailed  { get; set; } = true;
    public bool ShowSkipped { get; set; } = true;
    public bool ShowNotRun  { get; set; } = true;

    // ── Failure navigation ([ / ] keys) ──

    /// <summary>Index into the current failure list. -1 means no failure selected via navigation.</summary>
    public int CurrentFailureIndex { get; set; } = -1;

    // ── Grouping mode (G key) ──

    public GroupingMode Grouping { get; set; } = GroupingMode.ProjectNamespaceClass;

    // ── Expand/collapse (E key) ──

    public bool TreeExpanded { get; set; } = true;

    // ── Pinned tests (P key) ──

    public HashSet<string> PinnedTestFqns { get; } = new(StringComparer.Ordinal);
}

public enum GroupingMode
{
    ProjectNamespaceClass, // default: Project → Namespace → Class
    ByStatus,              // group roots: Passed / Failed / Skipped / Not Run
    Flat,                  // flat alphabetical list of all tests
}
