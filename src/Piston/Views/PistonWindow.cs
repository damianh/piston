using Piston.Core;
using Piston.Core.Models;
using Piston.Core.Orchestration;
using Piston.ViewModels;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;
using Color = Spectre.Console.Color;

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
        var countsMarkup = Controls.Markup("[dim]--/--/--[/]")
            .WithName("counts")
            .Build();
        var lastRunMarkup = Controls.Markup("[dim]Last run: --:--:--[/]")
            .WithName("lastrun")
            .Build();
        var buildTimeMarkup = Controls.Markup("[dim]build --s[/]")
            .WithName("buildtime")
            .Build();
        var testTimeMarkup = Controls.Markup("[dim]test --s[/]")
            .WithName("testtime")
            .Build();

        var headerBar = Controls.Toolbar()
            .Add(phaseMarkup)
            .AddSeparator()
            .Add(solutionMarkup)
            .AddSeparator()
            .Add(countsMarkup)
            .AddSeparator()
            .Add(lastRunMarkup)
            .AddSeparator()
            .Add(buildTimeMarkup)
            .AddSeparator()
            .Add(testTimeMarkup)
            .WithBackgroundColor(Color.Grey11)
            .StickyTop()
            .Build();

        // --- Main content: test tree (left) + detail panel (right) ---
        var detailContent = Controls.Markup(WelcomeMarkup())
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
            .Column(col => col.Width(40).Add(testTree))
            .WithSplitterAfter(0)
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

        var lastPhase          = (PistonPhase)(-1);
        var lastSolutionPath   = (string?)null;
        var lastRunTime        = (DateTimeOffset?)null;
        var lastPassed         = -1;
        var lastFailed         = -1;
        var lastSkipped        = -1;
        var lastSuiteCount     = -1;
        var lastSuiteHash      = 0;
        var lastFilter         = "\x00"; // sentinel — forces first render
        var lastBuild          = (BuildResult?)null;
        var lastRenderedRunTime  = (DateTimeOffset?)null;
        var lastBuildDuration  = (TimeSpan?)null;
        var lastTestDuration   = (TimeSpan?)null;

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
                else if (_state.Phase == PistonPhase.Watching
                         && _state.TestSuites.Count == 0
                         && !string.IsNullOrWhiteSpace(_state.LastTestRunnerError))
                {
                    var detailControl = window.FindControl<MarkupControl>("detail-content");
                    if (detailControl is not null)
                        detailControl.Text = RenderRunnerError(_state.LastTestRunnerError);
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
            var filterChanged = filter != lastFilter;

            if (passed != lastPassed || failed != lastFailed || skipped != lastSkipped
                || _state.LastRunTime != lastRunTime || filterChanged)
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

            // --- Last run time in toolbar ---
            if (_state.LastRunTime != lastRenderedRunTime)
            {
                lastRenderedRunTime = _state.LastRunTime;
                var lastRunControl = window.FindControl<MarkupControl>("lastrun");
                if (lastRunControl is not null)
                    lastRunControl.Text = lastRenderedRunTime.HasValue
                        ? $"[dim]Last run: {lastRenderedRunTime.Value.ToLocalTime():HH:mm:ss}[/]"
                        : "[dim]Last run: --:--:--[/]";
            }

            // --- Test counts in toolbar ---
            if (passed != lastPassed || failed != lastFailed || skipped != lastSkipped)
            {
                var countsControl = window.FindControl<MarkupControl>("counts");
                if (countsControl is not null)
                    countsControl.Text = CountsMarkup(passed, failed, skipped);
            }

            // --- Build duration in toolbar ---
            if (_state.LastBuildDuration != lastBuildDuration)
            {
                lastBuildDuration = _state.LastBuildDuration;
                var buildTimeControl = window.FindControl<MarkupControl>("buildtime");
                if (buildTimeControl is not null)
                    buildTimeControl.Text = lastBuildDuration.HasValue
                        ? $"[dim]build {lastBuildDuration.Value.TotalSeconds:F1}s[/]"
                        : "[dim]build --s[/]";
            }

            // --- Test duration in toolbar ---
            if (_state.LastTestDuration != lastTestDuration)
            {
                lastTestDuration = _state.LastTestDuration;
                var testTimeControl = window.FindControl<MarkupControl>("testtime");
                if (testTimeControl is not null)
                    testTimeControl.Text = lastTestDuration.HasValue
                        ? $"[dim]test {lastTestDuration.Value.TotalSeconds:F1}s[/]"
                        : "[dim]test --s[/]";
            }

            // --- Test tree (rebuild when suites/in-progress or filter change) ---
            // During Testing phase, show InProgressSuites for live feedback.
            var suites    = _state.Phase == PistonPhase.Testing && _state.InProgressSuites.Count > 0
                ? _state.InProgressSuites
                : _state.TestSuites;
            var suiteHash = ComputeSuitesHash(suites);
            if (suites.Count != lastSuiteCount || suiteHash != lastSuiteHash || filterChanged)
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

    private static string RenderRunnerError(string error)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[red3 bold]TEST RUNNER ERROR[/]");
        sb.AppendLine();
        sb.AppendLine("[dim]dotnet test produced no results. Runner output:[/]");
        sb.AppendLine();
        sb.AppendLine($"[red3]{EscapeMarkup(error)}[/]");
        return sb.ToString().TrimEnd();
    }

    private static string RenderBuildErrors(BuildResult? build)
    {
        if (build is null || build.Errors.Count == 0)
            return "[red3 bold]BUILD FAILED[/]\n\n[dim]No error details available.[/]";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[red3 bold]BUILD FAILED[/]");
        sb.AppendLine();
        sb.AppendLine($"[dim]{build.Errors.Count} error(s)  –  {build.Duration.TotalSeconds:F2}s[/]");
        sb.AppendLine();
        foreach (var error in build.Errors)
            sb.AppendLine($"[red3]{EscapeMarkup(error)}[/]");
        return sb.ToString().TrimEnd();
    }

    private static string EscapeMarkup(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");

    private static string WelcomeMarkup() =>
        "[dim]Watching for changes…[/]\n\n" +
        "[dim]  R[/]  force re-run\n" +
        "[dim]  F[/]  filter tests\n" +
        "[dim]  C[/]  clear results\n" +
        "[dim]  Q[/]  quit";

    private static string CountsMarkup(int passed, int failed, int skipped)
    {
        if (passed == 0 && failed == 0 && skipped == 0)
            return "[dim]--/--/--[/]";
        var p = passed  > 0 ? $"[green3]{passed}✓[/]"  : $"[dim]0✓[/]";
        var f = failed  > 0 ? $"[red3]{failed}✗[/]"    : $"[dim]0✗[/]";
        var s = skipped > 0 ? $"[gold1]{skipped}⊘[/]"  : $"[dim]0⊘[/]";
        return $"{p} {f} {s}";
    }

    private static string PhaseMarkup(PistonPhase phase) => phase switch
    {
        PistonPhase.Idle     => "[dim]◌ IDLE[/]",
        PistonPhase.Watching => "[green3]● WATCHING[/]",
        PistonPhase.Building => "[gold1]⟳ BUILDING[/]",
        PistonPhase.Testing  => "[cyan]⟳ TESTING[/]",
        PistonPhase.Error    => "[red3]✗ ERROR[/]",
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
