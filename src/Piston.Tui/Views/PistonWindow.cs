using Piston.Protocol;
using Piston.Protocol.Dtos;
using Piston.Protocol.Messages;
using Piston.Tui.ViewModels;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;
using Color = Spectre.Console.Color;

namespace Piston.Tui.Views;

public static class PistonWindow
{
    private static ConsoleWindowSystem? _windowSystem;
    private static IEngineClient? _client;
    private static Window? _mainWindow;
    private static ViewState? _viewState;
    private static string? _testFilter;

    public static void Create(
        ConsoleWindowSystem windowSystem,
        IEngineClient client)
    {
        _windowSystem = windowSystem;
        _client       = client;
        _viewState    = new ViewState();

        // --- Header bar (StickyTop) ---
        var phaseMarkup = Controls.Markup(PhaseMarkup(PistonPhaseDto.Idle))
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
        var progressMarkup = Controls.Markup("")
            .WithName("progress")
            .Build();
        var verifiedMarkup = Controls.Markup("")
            .WithName("verified")
            .Build();
        var failuresMarkup = Controls.Markup("")
            .WithName("failures")
            .Build();

        var headerBar = Controls.Toolbar()
            .Add(phaseMarkup)
            .AddSeparator()
            .Add(solutionMarkup)
            .AddSeparator()
            .Add(countsMarkup)
            .AddSeparator()
            .Add(progressMarkup)
            .AddSeparator()
            .Add(verifiedMarkup)
            .AddSeparator()
            .Add(failuresMarkup)
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
                StatusBarRenderer.Render(0, 0, 0, null, _testFilter))
            .WithName("statusbar")
            .StickyBottom()
            .Build();

        // --- Filter prompt (StickyBottom, hidden until F is pressed) ---
        var filterPrompt = Controls.Prompt("Filter: ")
            .WithName("filterprompt")
            .WithInput(_testFilter ?? string.Empty)
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

        // Update filter and push to client
        _testFilter = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _ = _client?.SetFilterAsync(_testFilter);
    }

    // ── Update loop ────────────────────────────────────────────────────────────

    private static async Task UpdateLoopAsync(Window window, CancellationToken ct)
    {
        if (_client is null) return;

        var lastPhase          = (PistonPhaseDto)(-1);
        var lastSolutionPath   = (string?)null;
        var lastRunTime        = (DateTimeOffset?)null;
        var lastPassed         = -1;
        var lastFailed         = -1;
        var lastSkipped        = -1;
        var lastSuiteCount     = -1;
        var lastSuiteHash      = 0;
        var lastFilter         = "\x00"; // sentinel — forces first render
        var lastBuild          = (BuildResultDto?)null;
        var lastRenderedRunTime  = (DateTimeOffset?)null;
        var lastBuildDurationMs  = (double?)null;
        var lastTestDurationMs   = (double?)null;
        var lastVerifiedCount  = -1;
        var lastTotalForVerify = -1;
        var lastCompletedTests = -1;
        var lastTotalTests     = -1;

        // ViewState change tracking
        var lastShowPassed     = true;
        var lastShowFailed     = true;
        var lastShowSkipped    = true;
        var lastShowNotRun     = true;
        var lastGrouping       = GroupingMode.ProjectNamespaceClass;
        var lastPinnedHash     = 0;
        var lastFileChangeTime = (DateTimeOffset?)null;

        while (!ct.IsCancellationRequested)
        {
            var snap = _client.CurrentSnapshot;
            if (snap is null)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            // --- Phase ---
            var phaseChanged = snap.Phase != lastPhase;
            if (phaseChanged)
            {
                lastPhase = snap.Phase;
                var phaseControl = window.FindControl<MarkupControl>("phase");
                if (phaseControl is not null)
                    phaseControl.Text = PhaseMarkup(snap.Phase);
            }

            // --- Build errors in detail panel when phase is Error ---
            var currentBuild = snap.LastBuild;
            if (currentBuild != lastBuild || phaseChanged)
            {
                lastBuild = currentBuild;
                if (snap.Phase == PistonPhaseDto.Error)
                {
                    var detailControl = window.FindControl<MarkupControl>("detail-content");
                    if (detailControl is not null)
                        detailControl.Text = RenderBuildErrors(currentBuild);
                }
                else if (snap.Phase == PistonPhaseDto.Watching
                         && snap.Suites.Count == 0
                         && !string.IsNullOrWhiteSpace(snap.LastTestRunnerError))
                {
                    var detailControl = window.FindControl<MarkupControl>("detail-content");
                    if (detailControl is not null)
                        detailControl.Text = RenderRunnerError(snap.LastTestRunnerError);
                }
            }

            // --- Solution name ---
            if (snap.SolutionPath != lastSolutionPath)
            {
                lastSolutionPath = snap.SolutionPath;
                var solutionControl = window.FindControl<MarkupControl>("solution");
                if (solutionControl is not null)
                    solutionControl.Text = lastSolutionPath is null
                        ? "[dim]No solution loaded[/]"
                        : $"[bold]{Path.GetFileName(lastSolutionPath)}[/]";
            }

            // --- Status bar (counts + last run + filter indicator) ---
            var passed  = snap.Suites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatusDto.Passed);
            var failed  = snap.Suites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatusDto.Failed);
            var skipped = snap.Suites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatusDto.Skipped);
            var filter  = _testFilter;
            var filterChanged = filter != lastFilter;

            if (passed != lastPassed || failed != lastFailed || skipped != lastSkipped
                || snap.LastRunTime != lastRunTime || filterChanged)
            {
                lastPassed  = passed;
                lastFailed  = failed;
                lastSkipped = skipped;
                lastRunTime = snap.LastRunTime;
                lastFilter  = filter;

                var statusControl = window.FindControl<MarkupControl>("statusbar");
                if (statusControl is not null)
                    statusControl.Text = StatusBarRenderer.Render(
                        passed, failed, skipped, lastRunTime, filter,
                        _viewState, snap.Phase,
                        snap.CompletedTests, snap.TotalExpectedTests,
                        _viewState?.CurrentFailureIndex ?? -1);
            }

            // --- Last run time in toolbar ---
            if (snap.LastRunTime != lastRenderedRunTime)
            {
                lastRenderedRunTime = snap.LastRunTime;
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

                // --- Failure summary in toolbar ---
                var failuresControl = window.FindControl<MarkupControl>("failures");
                if (failuresControl is not null)
                {
                    if (failed > 0)
                    {
                        var failedNames = snap.Suites
                            .SelectMany(s => s.Tests)
                            .Where(t => t.Status == TestStatusDto.Failed)
                            .Take(2)
                            .Select(t => EscapeMarkup(t.DisplayName))
                            .ToList();
                        var failText = string.Join(", ", failedNames);
                        if (failText.Length > 40) failText = failText[..37] + "...";
                        failuresControl.Text = $"[red3]{failText}[/]";
                    }
                    else
                    {
                        failuresControl.Text = "";
                    }
                }
            }

            // --- Build duration in toolbar ---
            if (snap.LastBuildDurationMs != lastBuildDurationMs)
            {
                lastBuildDurationMs = snap.LastBuildDurationMs;
                var buildTimeControl = window.FindControl<MarkupControl>("buildtime");
                if (buildTimeControl is not null)
                    buildTimeControl.Text = lastBuildDurationMs.HasValue
                        ? $"[dim]build {lastBuildDurationMs.Value / 1000.0:F1}s[/]"
                        : "[dim]build --s[/]";
            }

            // --- Test duration in toolbar ---
            if (snap.LastTestDurationMs != lastTestDurationMs)
            {
                lastTestDurationMs = snap.LastTestDurationMs;
                var testTimeControl = window.FindControl<MarkupControl>("testtime");
                if (testTimeControl is not null)
                    testTimeControl.Text = lastTestDurationMs.HasValue
                        ? $"[dim]test {lastTestDurationMs.Value / 1000.0:F1}s[/]"
                        : "[dim]test --s[/]";
            }

            // --- Progress bar in toolbar (during Testing phase) ---
            var completedTests = snap.CompletedTests;
            var totalTests     = snap.TotalExpectedTests;
            if (completedTests != lastCompletedTests || totalTests != lastTotalTests || phaseChanged)
            {
                lastCompletedTests = completedTests;
                lastTotalTests     = totalTests;
                var progressControl = window.FindControl<MarkupControl>("progress");
                if (progressControl is not null)
                {
                    if (snap.Phase == PistonPhaseDto.Testing && totalTests > 0)
                    {
                        var pct    = (int)(100.0 * completedTests / totalTests);
                        var filled = pct / 5; // 20-char bar
                        var empty  = 20 - filled;
                        progressControl.Text =
                            $"[cyan]{new string('█', filled)}[/][dim]{new string('░', empty)}[/] {pct}%";
                    }
                    else
                    {
                        progressControl.Text = "";
                    }
                }
            }

            // --- Verified count in toolbar ---
            var verifiedCount  = snap.VerifiedSinceChangeCount;
            var totalForVerify = snap.Suites.SelectMany(s => s.Tests).Count();
            if (verifiedCount != lastVerifiedCount || totalForVerify != lastTotalForVerify || phaseChanged)
            {
                lastVerifiedCount  = verifiedCount;
                lastTotalForVerify = totalForVerify;
                var verifiedControl = window.FindControl<MarkupControl>("verified");
                if (verifiedControl is not null)
                {
                    if (totalForVerify > 0)
                    {
                        var allVerified = verifiedCount >= totalForVerify;
                        var color = allVerified ? "green3" : "gold1";
                        verifiedControl.Text = $"[{color}]{verifiedCount}[/][dim]/{totalForVerify} verified[/]";
                    }
                    else
                    {
                        verifiedControl.Text = "";
                    }
                }
            }

            // --- Test tree (rebuild when suites/in-progress, filter, or view state changes) ---
            // During Testing phase, show InProgressSuites for live feedback.
            var suites    = snap.Phase == PistonPhaseDto.Testing && snap.InProgressSuites.Count > 0
                ? snap.InProgressSuites
                : snap.Suites;
            var suiteHash = ComputeSuitesHash(suites);

            // Compute view state change signals
            var showPassed  = _viewState?.ShowPassed  ?? true;
            var showFailed  = _viewState?.ShowFailed  ?? true;
            var showSkipped = _viewState?.ShowSkipped ?? true;
            var showNotRun  = _viewState?.ShowNotRun  ?? true;
            var grouping    = _viewState?.Grouping    ?? GroupingMode.ProjectNamespaceClass;
            var pinnedHash  = ComputePinnedHash(_viewState);
            var fileChange  = snap.LastFileChangeTime;

            var viewStateChanged = showPassed != lastShowPassed || showFailed != lastShowFailed
                || showSkipped != lastShowSkipped || showNotRun != lastShowNotRun
                || grouping != lastGrouping || pinnedHash != lastPinnedHash
                || fileChange != lastFileChangeTime;

            if (suites.Count != lastSuiteCount || suiteHash != lastSuiteHash || filterChanged || viewStateChanged)
            {
                lastSuiteCount      = suites.Count;
                lastSuiteHash       = suiteHash;
                lastShowPassed      = showPassed;
                lastShowFailed      = showFailed;
                lastShowSkipped     = showSkipped;
                lastShowNotRun      = showNotRun;
                lastGrouping        = grouping;
                lastPinnedHash      = pinnedHash;
                lastFileChangeTime  = fileChange;

                var treeControl = window.FindControl<TreeControl>("testtree");
                if (treeControl is not null)
                {
                    TestTreeBuilder.Rebuild(treeControl, suites, _testFilter, _viewState, fileChange);
                    // Re-apply expand/collapse preference after rebuild
                    if (_viewState is not null && !_viewState.TreeExpanded)
                        treeControl.CollapseAll();
                }
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

    private static string RenderBuildErrors(BuildResultDto? build)
    {
        if (build is null || build.Errors.Count == 0)
            return "[red3 bold]BUILD FAILED[/]\n\n[dim]No error details available.[/]";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[red3 bold]BUILD FAILED[/]");
        sb.AppendLine();
        sb.AppendLine($"[dim]{build.Errors.Count} error(s)  –  {build.DurationMs / 1000.0:F2}s[/]");
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
        "[dim]  G[/]  cycle grouping (P/N/C → Status → Flat)\n" +
        "[dim]  E[/]  expand/collapse all\n" +
        "[dim]  P[/]  pin/unpin selected test\n" +
        "[dim] 1-4[/] toggle Passed/Failed/Skipped/NotRun visibility\n" +
        "[dim] ]/[[/]  next/prev failure\n" +
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

    private static string PhaseMarkup(PistonPhaseDto phase) => phase switch
    {
        PistonPhaseDto.Idle     => "[dim]◌ IDLE[/]",
        PistonPhaseDto.Watching => "[green3]● WATCHING[/]",
        PistonPhaseDto.Building => "[gold1]⟳ BUILDING[/]",
        PistonPhaseDto.Testing  => "[cyan]⟳ TESTING[/]",
        PistonPhaseDto.Error    => "[red3]✗ ERROR[/]",
        _                       => "[dim]◌ IDLE[/]",
    };

    /// <summary>
    /// Cheap hash to detect when test suite results have changed without deep equality.
    /// </summary>
    private static int ComputeSuitesHash(IReadOnlyList<TestSuiteDto> suites)
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

    private static int ComputePinnedHash(ViewState? viewState)
    {
        if (viewState is null || viewState.PinnedTestFqns.Count == 0) return 0;
        var h = 0;
        foreach (var fqn in viewState.PinnedTestFqns.OrderBy(x => x))
            h = HashCode.Combine(h, fqn);
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
                _ = _client?.ForceRunAsync();
                break;

            case ConsoleKey.C:
                _ = _client?.ClearResultsAsync();
                break;

            case ConsoleKey.F:
                ShowFilterPrompt();
                break;

            // ── Status filter toggles ──────────────────────────────────────
            case ConsoleKey.D1 when _viewState is not null:
                _viewState.ShowPassed = !_viewState.ShowPassed;
                break;

            case ConsoleKey.D2 when _viewState is not null:
                _viewState.ShowFailed = !_viewState.ShowFailed;
                break;

            case ConsoleKey.D3 when _viewState is not null:
                _viewState.ShowSkipped = !_viewState.ShowSkipped;
                break;

            case ConsoleKey.D4 when _viewState is not null:
                _viewState.ShowNotRun = !_viewState.ShowNotRun;
                break;

            // ── Grouping mode ───────────────────────────────────────────────
            case ConsoleKey.G when _viewState is not null:
                _viewState.Grouping = _viewState.Grouping switch
                {
                    GroupingMode.ProjectNamespaceClass => GroupingMode.ByStatus,
                    GroupingMode.ByStatus              => GroupingMode.Flat,
                    _                                  => GroupingMode.ProjectNamespaceClass,
                };
                break;

            // ── Expand/collapse all ─────────────────────────────────────────
            case ConsoleKey.E when _viewState is not null:
                _viewState.TreeExpanded = !_viewState.TreeExpanded;
                var treeForExpand = _mainWindow?.FindControl<TreeControl>("testtree");
                if (treeForExpand is not null)
                {
                    if (_viewState.TreeExpanded)
                        treeForExpand.ExpandAll();
                    else
                        treeForExpand.CollapseAll();
                }
                break;

            // ── Pin/unpin selected test ────────────────────────────────────
            case ConsoleKey.P when _viewState is not null:
                var treeForPin = _mainWindow?.FindControl<TreeControl>("testtree");
                if (treeForPin?.SelectedNode?.Tag is TestNodeTag.Test pinTest)
                {
                    var fqn = pinTest.Result.FullyQualifiedName;
                    if (!_viewState.PinnedTestFqns.Remove(fqn))
                        _viewState.PinnedTestFqns.Add(fqn);
                }
                break;
        }

        // ── Failure navigation (bracket keys — KeyChar match, not Key enum) ──
        if (_client is not null && _viewState is not null)
        {
            if (key.KeyChar == ']')
                NavigateToFailure(+1);
            else if (key.KeyChar == '[')
                NavigateToFailure(-1);
        }
    }

    private static void NavigateToFailure(int direction)
    {
        if (_client is null || _viewState is null || _mainWindow is null) return;

        var snap = _client.CurrentSnapshot;
        if (snap is null) return;

        var failures = snap.Suites
            .SelectMany(s => s.Tests)
            .Where(t => t.Status == TestStatusDto.Failed)
            .ToList();

        if (failures.Count == 0)
        {
            _viewState.CurrentFailureIndex = -1;
            return;
        }

        // Advance index
        if (_viewState.CurrentFailureIndex < 0)
            _viewState.CurrentFailureIndex = direction > 0 ? 0 : failures.Count - 1;
        else
            _viewState.CurrentFailureIndex = ((_viewState.CurrentFailureIndex + direction) % failures.Count + failures.Count) % failures.Count;

        var target = failures[_viewState.CurrentFailureIndex];
        var treeControl = _mainWindow.FindControl<TreeControl>("testtree");
        if (treeControl is null) return;

        var node = treeControl.FindNodeByTag(new TestNodeTag.Test(target));
        if (node is not null)
        {
            treeControl.SelectNode(node);
            treeControl.EnsureNodeVisible(node);
        }
    }

    private static void ShowFilterPrompt()
    {
        if (_mainWindow is null) return;

        var prompt = _mainWindow.FindControl<PromptControl>("filterprompt");
        if (prompt is null) return;

        // Pre-populate with current filter value
        prompt.Input = _testFilter ?? string.Empty;
        prompt.Visible = true;
        prompt.SetFocus(true, SharpConsoleUI.Controls.FocusReason.Programmatic);
    }
}
