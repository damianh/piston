using Piston.Protocol.Dtos;
using Piston.Tui.ViewModels;

namespace Piston.Tui.Views;

/// <summary>
/// Renders the sticky-bottom status bar markup string using protocol DTO types.
/// </summary>
public static class StatusBarRenderer
{
    /// <summary>
    /// Builds the full status bar markup including counts, last run time, filter indicator,
    /// status-filter toggles, grouping mode, failure navigation, and hotkey hints.
    /// </summary>
    public static string Render(
        int passed, int failed, int skipped,
        DateTimeOffset? lastRunTime,
        string? filter = null,
        ViewState? viewState = null,
        PistonPhaseDto phase = PistonPhaseDto.Idle,
        int completedTests = 0,
        int totalExpectedTests = 0,
        int currentFailureIndex = -1)
    {
        var sb = new System.Text.StringBuilder();

        // Section 1: counts or progress bar during testing
        if (phase == PistonPhaseDto.Testing && totalExpectedTests > 0)
        {
            var pct    = (int)(100.0 * completedTests / totalExpectedTests);
            var filled = pct / 5;
            var empty  = 20 - filled;
            sb.Append($"[cyan]{new string('█', filled)}[/][dim]{new string('░', empty)}[/] {pct}% ({completedTests}/{totalExpectedTests})");
        }
        else
        {
            sb.Append(CountsMarkup(passed, failed, skipped));
        }

        // Section 2: status filter toggles
        var p = viewState?.ShowPassed  ?? true;
        var f = viewState?.ShowFailed  ?? true;
        var s = viewState?.ShowSkipped ?? true;
        var n = viewState?.ShowNotRun  ?? true;
        sb.Append("  │  ");
        sb.Append(p ? "[green3]1[/]✓" : "[dim]1✓[/]");
        sb.Append(' ');
        sb.Append(f ? "[red3]2[/]✗" : "[dim]2✗[/]");
        sb.Append(' ');
        sb.Append(s ? "[gold1]3[/]●" : "[dim]3●[/]");
        sb.Append(' ');
        sb.Append(n ? "[grey]4[/]◌" : "[dim]4◌[/]");

        // Section 3: grouping mode
        var groupLabel = (viewState?.Grouping ?? GroupingMode.ProjectNamespaceClass) switch
        {
            GroupingMode.ByStatus => "Status",
            GroupingMode.Flat     => "Flat",
            _                     => "P/N/C",
        };
        sb.Append($"  │  [dim]Group: {groupLabel}[/]");

        // Section 4: failure navigation indicator
        if (currentFailureIndex >= 0 && failed > 0)
            sb.Append($"  │  [red3]Failure {currentFailureIndex + 1}/{failed}[/]");

        // Section 5: filter indicator
        if (filter is not null)
            sb.Append($"  │  [dim]filter:[/] [gold1]{Escape(filter)}[/]");

        // Section 6: last run time
        sb.Append($"  │  {LastRunMarkup(lastRunTime)}");

        // Section 7: key hints
        sb.Append("  │  [grey]R[/]un  [grey]F[/]ilter  [grey]C[/]lear  [grey]G[/]roup  [grey]E[/]xp  [grey]P[/]in  [grey]Q[/]uit");

        return sb.ToString();
    }

    private static string CountsMarkup(int passed, int failed, int skipped)
    {
        var total = passed + failed + skipped;
        if (total == 0)
            return "[dim]No tests[/]";

        return $"[green3]✓ {passed}[/]  [red3]✗ {failed}[/]  [gold1]● {skipped}[/]";
    }

    private static string LastRunMarkup(DateTimeOffset? lastRunTime) =>
        lastRunTime is null
            ? "[dim]Never run[/]"
            : $"[dim]Last run: {lastRunTime.Value.ToLocalTime():HH:mm:ss}[/]";

    private static string Escape(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");
}

