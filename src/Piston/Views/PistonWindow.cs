using Piston.Core;
using Piston.Core.Models;
using Piston.Core.Orchestration;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace Piston.Views;

public static class PistonWindow
{
    private static ConsoleWindowSystem? _windowSystem;
    private static IPistonOrchestrator? _orchestrator;
    private static PistonState? _state;

    public static void Create(
        ConsoleWindowSystem windowSystem,
        PistonState state,
        IPistonOrchestrator orchestrator)
    {
        _windowSystem = windowSystem;
        _state = state;
        _orchestrator = orchestrator;

        // --- Header bar (StickyTop) ---
        var phaseMarkup = Controls.Markup(PhaseMarkup(PistonPhase.Idle))
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
            .WithAsyncWindowThread(async (win, ct) => await UpdateLoopAsync(win, ct))
            .Build();

        windowSystem.AddWindow(mainWindow);
    }

    private static async Task UpdateLoopAsync(Window window, CancellationToken ct)
    {
        if (_state is null) return;

        var lastPhase = (PistonPhase)(-1);
        var lastSolutionPath = (string?)null;
        var lastRunTime = (DateTimeOffset?)null;
        var lastPassed = -1;
        var lastFailed = -1;
        var lastSkipped = -1;

        while (!ct.IsCancellationRequested)
        {
            var changed = false;

            if (_state.Phase != lastPhase)
            {
                lastPhase = _state.Phase;
                var phaseControl = window.FindControl<MarkupControl>("phase");
                if (phaseControl is not null)
                    phaseControl.Text = PhaseMarkup(_state.Phase);
                changed = true;
            }

            if (_state.SolutionPath != lastSolutionPath)
            {
                lastSolutionPath = _state.SolutionPath;
                var solutionControl = window.FindControl<MarkupControl>("solution");
                if (solutionControl is not null)
                    solutionControl.Text = lastSolutionPath is null
                        ? "[dim]No solution loaded[/]"
                        : $"[bold]{Path.GetFileName(lastSolutionPath)}[/]";
                changed = true;
            }

            if (_state.LastRunTime != lastRunTime)
            {
                lastRunTime = _state.LastRunTime;
                var lastRunControl = window.FindControl<MarkupControl>("lastrun");
                if (lastRunControl is not null)
                    lastRunControl.Text = lastRunTime is null
                        ? "[dim]Last run: --:--:--[/]"
                        : $"[dim]Last run: {lastRunTime.Value.ToLocalTime():HH:mm:ss}[/]";
                changed = true;
            }

            var passed = _state.TotalPassed;
            var failed = _state.TotalFailed;
            var skipped = _state.TotalSkipped;

            if (passed != lastPassed || failed != lastFailed || skipped != lastSkipped)
            {
                lastPassed = passed;
                lastFailed = failed;
                lastSkipped = skipped;

                var statusControl = window.FindControl<MarkupControl>("statusbar");
                if (statusControl is not null)
                    statusControl.Text = StatusBarMarkup(passed, failed, skipped);
                changed = true;
            }

            _ = changed; // framework re-renders automatically on next tick

            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    private static string PhaseMarkup(PistonPhase phase) => phase switch
    {
        PistonPhase.Idle     => "[dim]◌ IDLE[/]",
        PistonPhase.Watching => "[green]● WATCHING[/]",
        PistonPhase.Building => "[yellow]⟳ BUILDING[/]",
        PistonPhase.Testing  => "[cyan]⟳ TESTING[/]",
        PistonPhase.Error    => "[red]✗ ERROR[/]",
        _                    => "[dim]◌ IDLE[/]",
    };

    private static string StatusBarMarkup(int passed, int failed, int skipped) =>
        $"[green]✓ {passed}[/]  [red]✗ {failed}[/]  [yellow]● {skipped}[/]  │  [grey]R[/]un  [grey]F[/]ilter  [grey]C[/]lear  [grey]Q[/]uit";

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

            case ConsoleKey.R:
                // TODO Phase 3: trigger force run
                _ = _orchestrator?.ForceRunAsync();
                break;

            // TODO: case ConsoleKey.F → open filter
            // TODO: case ConsoleKey.C → clear results
        }
    }
}
