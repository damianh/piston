using Piston.Core.Models;

namespace Piston.Views;

/// <summary>
/// Renders the sticky-bottom status bar markup string from <see cref="PistonState"/>.
/// </summary>
public static class StatusBarRenderer
{
    /// <summary>
    /// Builds the full status bar markup including counts, last run time, filter indicator, and hotkey hints.
    /// </summary>
    public static string Render(int passed, int failed, int skipped, DateTimeOffset? lastRunTime, string? filter = null)
    {
        var filterPart = filter is not null
            ? $"  [dim]filter:[/] [gold1]{Escape(filter)}[/]"
            : string.Empty;

        return $"{CountsMarkup(passed, failed, skipped)}{filterPart}  │  {LastRunMarkup(lastRunTime)}  │  " +
               $"[grey]R[/]un  [grey]F[/]ilter  [grey]C[/]lear  [grey]Q[/]uit";
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
