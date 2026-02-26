using Piston.Core;
using Piston.Core.Models;
using Piston.Core.Orchestration;
using Piston.ViewModels;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;

namespace Piston.Views;

public static class PistonWindow
{
    private static ConsoleWindowSystem? _windowSystem;
    private static IPistonOrchestrator? _orchestrator;
    private static PistonState? _state;
    private static Window? _mainWindow;

    public static void Create(
        ConsoleWindowSystem windowSystem,
        PistonState state,
        IPistonOrchestrator orchestrator,
        PistonOptions? options = null)
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
        var detailContent = Controls.Markup("[dim]Select a test to view details.[/]")
            .WithName("detail-content")
            .Build();

        var detailPanel = Controls.ScrollablePanel()
            .WithName("detail")
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .AddControl(detailContent)
            .Build();

        var testTree = Controls.Tree()
            .WithName("testtree")
            .AddRootNode(new TreeNode("No tests discovered"))
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .OnSelectedNodeChanged((sender, args, window) =>
                OnTreeSelectionChanged(args, window))
            .Build();

        var mainGrid = Controls.HorizontalGrid()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(col => col.Width(30).Add(testTree))
            .Column(col => col.Width(1))
            .Column(col => col.Add(detailPanel))
            .Build();

        // --- Status bar (StickyBottom) ---
        var statusBar = Controls.Markup(
                StatusBarRenderer.Render(0, 0, 0, null, state.TestFilter))
            .WithName("statusbar")
            .StickyBottom()
            .Build();

        // --- Filter prompt (StickyBottom, hidden until F is pressed) ---
        var filterPrompt = Controls.Prompt("Filter: ")
            .WithName("filterprompt")
            .WithInput(state.TestFilter ?? string.Empty)
            .WithWidth(50)
            .StickyBottom()
            .Visible(false)
            .OnEntered((sender, value, window) => OnFilterEntered(value, window))
            .Build();

        // --- Build and register window ---
        _mainWindow = new WindowBuilder(windowSystem)
            .WithTitle("Piston")
            .Borderless()
            .Maximized()
            .AddControl(headerBar)
            .AddControl(mainGrid)
            .AddControl(statusBar)
            .AddControl(filterPrompt)
            .OnKeyPressed(HandleKeyPress)
            .WithAsyncWindowThread(async (win, ct) => await UpdateLoopAsync(win, ct))
            .Build();

        windowSystem.AddWindow(_mainWindow);
    }

    // ── Tree selection ─────────────────────────────────────────────────────────

    private static void OnTreeSelectionChanged(TreeNodeEventArgs args, Window window)
    {
        var detailControl = window.FindControl<MarkupControl>("detail-content");
        if (detailControl is null) return;

        var tag = args.Node?.Tag as TestNodeTag;
        TestDetailRenderer.Render(detailControl, tag);
    }

    // ── Filter prompt handler ──────────────────────────────────────────────────

    private static void OnFilterEntered(string value, Window window)
    {
        // Hide the prompt
        var prompt = window.FindControl<PromptControl>("filterprompt");
        if (prompt is not null)
            prompt.Visible = false;

        // Update state
        if (_state is not null)
        {
            _state.TestFilter = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _state.NotifyChanged();
        }
    }

    // ── Update loop ────────────────────────────────────────────────────────────

    private static async Task UpdateLoopAsync(Window window, CancellationToken ct)
    {
        if (_state is null) return;

        var lastPhase        = (PistonPhase)(-1);
        var lastSolutionPath = (string?)null;
        var lastRunTime      = (DateTimeOffset?)null;
        var lastPassed       = -1;
        var lastFailed       = -1;
        var lastSkipped      = -1;
        var lastSuiteCount   = -1;
        var lastSuiteHash    = 0;
        var lastFilter       = "\x00"; // sentinel — forces first render
        var lastBuild        = (BuildResult?)null;

        while (!ct.IsCancellationRequested)
        {
            // --- Phase ---
            var phaseChanged = _state.Phase != lastPhase;
            if (phaseChanged)
            {
                lastPhase = _state.Phase;
                var phaseControl = window.FindControl<MarkupControl>("phase");
                if (phaseControl is not null)
                    phaseControl.Text = PhaseMarkup(_state.Phase);
            }

            // --- Build errors in detail panel when phase is Error ---
            var currentBuild = _state.LastBuild;
            if (currentBuild != lastBuild || phaseChanged)
            {
                lastBuild = currentBuild;
                if (_state.Phase == PistonPhase.Error)
                {
                    var detailControl = window.FindControl<MarkupControl>("detail-content");
                    if (detailControl is not null)
                        detailControl.Text = RenderBuildErrors(currentBuild);
                }
            }

            // --- Solution name ---
            if (_state.SolutionPath != lastSolutionPath)
            {
                lastSolutionPath = _state.SolutionPath;
                var solutionControl = window.FindControl<MarkupControl>("solution");
                if (solutionControl is not null)
                    solutionControl.Text = lastSolutionPath is null
                        ? "[dim]No solution loaded[/]"
                        : $"[bold]{Path.GetFileName(lastSolutionPath)}[/]";
            }

            // --- Status bar (counts + last run + filter indicator) ---
            var passed  = _state.TotalPassed;
            var failed  = _state.TotalFailed;
            var skipped = _state.TotalSkipped;
            var filter  = _state.TestFilter;

            if (passed != lastPassed || failed != lastFailed || skipped != lastSkipped
                || _state.LastRunTime != lastRunTime || filter != lastFilter)
            {
                lastPassed  = passed;
                lastFailed  = failed;
                lastSkipped = skipped;
                lastRunTime = _state.LastRunTime;
                lastFilter  = filter;

                var statusControl = window.FindControl<MarkupControl>("statusbar");
                if (statusControl is not null)
                    statusControl.Text = StatusBarRenderer.Render(passed, failed, skipped, lastRunTime, filter);
            }

            // --- Test tree (rebuild when suites or filter change) ---
            var suites    = _state.TestSuites;
            var suiteHash = ComputeSuitesHash(suites);
            if (suites.Count != lastSuiteCount || suiteHash != lastSuiteHash || filter != lastFilter)
            {
                lastSuiteCount = suites.Count;
                lastSuiteHash  = suiteHash;

                var treeControl = window.FindControl<TreeControl>("testtree");
                if (treeControl is not null)
                    TestTreeBuilder.Rebuild(treeControl, suites, _state.TestFilter);
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string RenderBuildErrors(BuildResult? build)
    {
        if (build is null || build.Errors.Count == 0)
            return "[red bold]BUILD FAILED[/]\n\n[dim]No error details available.[/]";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[red bold]BUILD FAILED[/]");
        sb.AppendLine();
        sb.AppendLine($"[dim]{build.Errors.Count} error(s)  –  {build.Duration.TotalSeconds:F2}s[/]");
        sb.AppendLine();
        foreach (var error in build.Errors)
            sb.AppendLine($"[red]{EscapeMarkup(error)}[/]");
        return sb.ToString().TrimEnd();
    }

    private static string EscapeMarkup(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");

    private static string PhaseMarkup(PistonPhase phase) => phase switch
    {
        PistonPhase.Idle     => "[dim]◌ IDLE[/]",
        PistonPhase.Watching => "[green]● WATCHING[/]",
        PistonPhase.Building => "[yellow]⟳ BUILDING[/]",
        PistonPhase.Testing  => "[cyan]⟳ TESTING[/]",
        PistonPhase.Error    => "[red]✗ ERROR[/]",
        _                    => "[dim]◌ IDLE[/]",
    };

    /// <summary>
    /// Cheap hash to detect when test suite results have changed without deep equality.
    /// </summary>
    private static int ComputeSuitesHash(IReadOnlyList<TestSuite> suites)
    {
        var h = 0;
        foreach (var s in suites)
        {
            h = HashCode.Combine(h, s.Name, s.Tests.Count);
            foreach (var t in s.Tests)
                h = HashCode.Combine(h, t.FullyQualifiedName, (int)t.Status);
        }
        return h;
    }

    // ── Key handler ────────────────────────────────────────────────────────────

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
                _ = _orchestrator?.ForceRunAsync();
                break;

            case ConsoleKey.C:
                // Clear results
                if (_state is not null)
                {
                    _state.TestSuites  = [];
                    _state.LastRunTime = null;
                    _state.NotifyChanged();
                }
                break;

            case ConsoleKey.F:
                ShowFilterPrompt();
                break;
        }
    }

    private static void ShowFilterPrompt()
    {
        if (_mainWindow is null) return;

        var prompt = _mainWindow.FindControl<PromptControl>("filterprompt");
        if (prompt is null) return;

        // Pre-populate with current filter value
        prompt.Input = _state?.TestFilter ?? string.Empty;
        prompt.Visible = true;
        prompt.SetFocus(true, SharpConsoleUI.Controls.FocusReason.Programmatic);
    }
}
