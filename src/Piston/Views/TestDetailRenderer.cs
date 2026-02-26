using Piston.Core.Models;
using Piston.ViewModels;
using SharpConsoleUI.Controls;

namespace Piston.Views;

/// <summary>
/// Renders a selected <see cref="TestNodeTag"/> into a detail panel
/// <see cref="MarkupControl"/>.
/// </summary>
public static class TestDetailRenderer
{
    /// <summary>
    /// Updates <paramref name="detailControl"/> based on the currently selected
    /// tree node tag. Pass <c>null</c> to show the default "select a test" hint.
    /// </summary>
    public static void Render(MarkupControl detailControl, TestNodeTag? tag)
    {
        detailControl.Text = tag switch
        {
            TestNodeTag.Test t  => RenderTest(t.Result),
            TestNodeTag.Suite s => RenderSuite(s.TestSuite),
            TestNodeTag.Group g => RenderGroup(g.Name),
            null                => "[dim]Select a test to view details.[/]",
            _                   => "[dim]Select a test to view details.[/]",
        };
    }

    // ── Renderers ──────────────────────────────────────────────────────────────

    private static string RenderTest(TestResult test)
    {
        var sb = new System.Text.StringBuilder();

        // Header: FQN
        sb.AppendLine($"[bold]{Escape(test.FullyQualifiedName)}[/]");
        sb.AppendLine();

        // Status + duration
        var statusMarkup = test.Status switch
        {
            TestStatus.Passed  => "[green3]PASSED[/]",
            TestStatus.Failed  => "[red3]FAILED[/]",
            TestStatus.Skipped => "[gold1]SKIPPED[/]",
            TestStatus.Running => "[cyan]RUNNING[/]",
            _                  => "[dim]NOT RUN[/]",
        };
        sb.AppendLine($"Status:    {statusMarkup}");
        sb.AppendLine($"Duration:  [dim]{test.Duration.TotalMilliseconds:F0}ms[/]");

        // Error info
        if (!string.IsNullOrWhiteSpace(test.ErrorMessage))
        {
            sb.AppendLine();
            sb.AppendLine("[red3]Error:[/]");
            sb.AppendLine(Escape(test.ErrorMessage));
        }

        // Stack trace
        if (!string.IsNullOrWhiteSpace(test.StackTrace))
        {
            sb.AppendLine();
            sb.AppendLine("[dim]Stack Trace:[/]");
            sb.AppendLine($"[dim]{Escape(test.StackTrace)}[/]");
        }

        // Stdout
        if (!string.IsNullOrWhiteSpace(test.Output))
        {
            sb.AppendLine();
            sb.AppendLine("[dim]Output:[/]");
            sb.AppendLine($"[dim]{Escape(test.Output)}[/]");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderSuite(TestSuite suite)
    {
        var passed  = suite.Tests.Count(t => t.Status == TestStatus.Passed);
        var failed  = suite.Tests.Count(t => t.Status == TestStatus.Failed);
        var skipped = suite.Tests.Count(t => t.Status == TestStatus.Skipped);

        return $"[bold]{Escape(suite.Name)}[/]\n\n" +
               $"Tests:    [bold]{suite.Tests.Count}[/]\n" +
               $"Passed:   [green3]{passed}[/]\n" +
               $"Failed:   [red3]{failed}[/]\n" +
               $"Skipped:  [gold1]{skipped}[/]\n" +
               $"Duration: [dim]{suite.TotalDuration.TotalSeconds:F2}s[/]";
    }

    private static string RenderGroup(string name) =>
        string.IsNullOrEmpty(name)
            ? "[dim]Select a test to view details.[/]"
            : $"[bold]{Escape(name)}[/]\n\n[dim]Select a test method to view details.[/]";

    // ── Utility ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Escapes characters that have special meaning in Spectre.Console markup.
    /// </summary>
    private static string Escape(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");
}
