using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace Piston.Views;

public static class PistonWindow
{
    private static ConsoleWindowSystem? _windowSystem;

    public static void Create(ConsoleWindowSystem windowSystem)
    {
        _windowSystem = windowSystem;

        // --- Header bar (StickyTop) ---
        var phaseMarkup = Controls.Markup("[dim]◌ IDLE[/]")
            .WithName("phase")
            .Build();
        var solutionMarkup = Controls.Markup("[dim]No solution loaded[/]")
            .WithName("solution")
            .Build();
        var lastRunMarkup = Controls.Markup("[dim]Last run: --:--:--[/]")
            .WithName("lastrun")
            .Build();

        var headerBar = Controls.HorizontalGrid()
            .StickyTop()
            .Column(col => col.Add(phaseMarkup))
            .Column(col => col.Add(solutionMarkup))
            .Column(col => col.Add(lastRunMarkup))
            .Build();

        // --- Main content: test tree (left) + detail panel (right) ---
        var testTree = Controls.Tree()
            .WithName("testtree")
            .AddRootNode(new TreeNode("No tests discovered"))
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Build();

        var detailContent = Controls.Markup("Select a test to view details.")
            .WithName("detail-content")
            .Build();

        var detailPanel = Controls.ScrollablePanel()
            .WithName("detail")
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .AddControl(detailContent)
            .Build();

        var mainGrid = Controls.HorizontalGrid()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(col => col.Width(30).Add(testTree))
            .Column(col => col.Width(1))
            .Column(col => col.Add(detailPanel))
            .Build();

        // --- Status bar (StickyBottom) ---
        var statusBar = Controls.Markup(
                "[green]✓ 0[/]  [red]✗ 0[/]  [yellow]● 0[/]  │  [grey]R[/]un  [grey]F[/]ilter  [grey]C[/]lear  [grey]Q[/]uit")
            .WithName("statusbar")
            .StickyBottom()
            .Build();

        // --- Build and register window ---
        var mainWindow = new WindowBuilder(windowSystem)
            .WithTitle("Piston")
            .Borderless()
            .Maximized()
            .AddControl(headerBar)
            .AddControl(mainGrid)
            .AddControl(statusBar)
            .OnKeyPressed(HandleKeyPress)
            .Build();

        windowSystem.AddWindow(mainWindow);
    }

    private static void HandleKeyPress(object? sender, KeyPressedEventArgs e)
    {
        var key = e.KeyInfo;

        switch (key.Key)
        {
            case ConsoleKey.Q:
                _windowSystem?.Shutdown();
                break;

            case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                _windowSystem?.Shutdown();
                break;

            // TODO: case ConsoleKey.R → trigger run
            // TODO: case ConsoleKey.F → open filter
            // TODO: case ConsoleKey.C → clear results
        }
    }
}
