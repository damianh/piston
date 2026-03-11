using System.Text.RegularExpressions;
using Piston.Core.Models;
using Piston.ViewModels;
using SharpConsoleUI.Controls;

namespace Piston.Views;

/// <summary>
/// Builds a SharpConsoleUI <see cref="TreeControl"/> node hierarchy from
/// <see cref="TestSuite"/> data. Supports multiple grouping strategies, status filters,
/// duration display, pinned tests, and stale-result dimming.
/// </summary>
public static class TestTreeBuilder
{
    /// <summary>
    /// Replaces the contents of <paramref name="tree"/> with nodes built from
    /// <paramref name="suites"/>. Safe to call from the async window thread.
    /// </summary>
    public static void Rebuild(
        TreeControl tree,
        IReadOnlyList<TestSuite> suites,
        string? filter = null,
        ViewState? viewState = null,
        DateTimeOffset? lastFileChangeTime = null)
    {
        var matcher = BuildMatcher(filter);

        tree.Clear();

        if (suites.Count == 0)
        {
            tree.AddRootNode(new TreeNode("No tests discovered") { Tag = new TestNodeTag.Group("") });
            return;
        }

        switch (viewState?.Grouping ?? GroupingMode.ProjectNamespaceClass)
        {
            case GroupingMode.ByStatus:
                RebuildByStatus(tree, suites, matcher, viewState, lastFileChangeTime);
                break;
            case GroupingMode.Flat:
                RebuildFlat(tree, suites, matcher, viewState, lastFileChangeTime);
                break;
            default:
                RebuildByProjectNsClass(tree, suites, matcher, viewState, lastFileChangeTime);
                break;
        }
    }

    // ── Grouping strategies ──────────────────────────────────────────────────

    private static void RebuildByProjectNsClass(
        TreeControl tree,
        IReadOnlyList<TestSuite> suites,
        Func<string, bool>? matcher,
        ViewState? viewState,
        DateTimeOffset? lastFileChangeTime)
    {
        // Pinned section always at top, regardless of filters
        if (viewState is not null)
            AddPinnedSection(tree, suites, viewState, lastFileChangeTime);

        var anyVisible = false;

        foreach (var suite in suites)
        {
            var visibleTests = GetVisibleTests(suite.Tests, matcher, viewState);
            if (visibleTests.Count == 0) continue;
            anyVisible = true;

            var suiteNode = new TreeNode(SuiteLabel(suite, visibleTests))
            {
                Tag = new TestNodeTag.Suite(suite),
                IsExpanded = true,
            };

            // Group by namespace, then by class within each namespace
            var byNamespace = visibleTests
                .GroupBy(t => NamespaceKey(t.FullyQualifiedName))
                .OrderBy(g => g.Key);

            foreach (var nsGroup in byNamespace)
            {
                var nsTests = nsGroup.ToList();

                IEnumerable<(string classKey, IEnumerable<TestResult> tests)> classGroups =
                    nsGroup
                        .GroupBy(t => ClassKey(t.FullyQualifiedName))
                        .OrderBy(g => g.Key)
                        .Select(g => (g.Key, (IEnumerable<TestResult>)g));

                if (string.IsNullOrEmpty(nsGroup.Key))
                {
                    foreach (var (classKey, classTests) in classGroups)
                    {
                        var classTestList = classTests.ToList();
                        var classNode = new TreeNode(ClassLabel(classKey, classTestList))
                        {
                            Tag = new TestNodeTag.Group(classKey),
                            IsExpanded = true,
                        };
                        AddTestLeaves(classNode, classTestList, suite, viewState, lastFileChangeTime);
                        suiteNode.AddChild(classNode);
                    }
                }
                else
                {
                    var nsNode = new TreeNode(NamespaceLabel(nsGroup.Key, nsTests))
                    {
                        Tag = new TestNodeTag.Group(nsGroup.Key),
                        IsExpanded = true,
                    };

                    foreach (var (classKey, classTests) in classGroups)
                    {
                        var classTestList = classTests.ToList();
                        var classNode = new TreeNode(ClassLabel(classKey, classTestList))
                        {
                            Tag = new TestNodeTag.Group(classKey),
                            IsExpanded = true,
                        };
                        AddTestLeaves(classNode, classTestList, suite, viewState, lastFileChangeTime);
                        nsNode.AddChild(classNode);
                    }

                    suiteNode.AddChild(nsNode);
                }
            }

            tree.AddRootNode(suiteNode);
        }

        if (!anyVisible)
            AddNoResultsNode(tree, null);
    }

    private static void RebuildByStatus(
        TreeControl tree,
        IReadOnlyList<TestSuite> suites,
        Func<string, bool>? matcher,
        ViewState? viewState,
        DateTimeOffset? lastFileChangeTime)
    {
        // Pinned section always at top
        if (viewState is not null)
            AddPinnedSection(tree, suites, viewState, lastFileChangeTime);

        var allTests = suites
            .SelectMany(s => s.Tests.Select(t => (Suite: s, Test: t)))
            .Where(x => matcher is null || matcher(x.Test.FullyQualifiedName))
            .ToList();

        if (allTests.Count == 0)
        {
            AddNoResultsNode(tree, null);
            return;
        }

        var groups = new[]
        {
            ("✗ Failed",  TestStatus.Failed,  viewState?.ShowFailed  ?? true),
            ("⟳ Running", TestStatus.Running, true),
            ("✓ Passed",  TestStatus.Passed,  viewState?.ShowPassed  ?? true),
            ("● Skipped", TestStatus.Skipped, viewState?.ShowSkipped ?? true),
            ("◌ Not Run", TestStatus.NotRun,  viewState?.ShowNotRun  ?? true),
        };

        foreach (var (label, status, show) in groups)
        {
            if (!show) continue;
            var grouped = allTests.Where(x => x.Test.Status == status).ToList();
            if (grouped.Count == 0) continue;

            var icon = status switch
            {
                TestStatus.Failed  => "[red3]",
                TestStatus.Running => "[cyan]",
                TestStatus.Passed  => "[green3]",
                TestStatus.Skipped => "[gold1]",
                _                  => "[dim]",
            };
            var rootNode = new TreeNode($"{icon}{label}[/] [dim]({grouped.Count})[/]")
            {
                Tag = new TestNodeTag.Group(label),
                IsExpanded = true,
            };

            foreach (var (suite, test) in grouped.OrderBy(x => x.Test.DisplayName))
            {
                var isStale  = IsStale(suite, lastFileChangeTime);
                var isPinned = viewState?.PinnedTestFqns.Contains(test.FullyQualifiedName) ?? false;
                rootNode.AddChild(new TreeNode(TestLabel(test, isStale, isPinned)) { Tag = new TestNodeTag.Test(test) });
            }

            tree.AddRootNode(rootNode);
        }
    }

    private static void RebuildFlat(
        TreeControl tree,
        IReadOnlyList<TestSuite> suites,
        Func<string, bool>? matcher,
        ViewState? viewState,
        DateTimeOffset? lastFileChangeTime)
    {
        // Pinned section always at top
        if (viewState is not null)
            AddPinnedSection(tree, suites, viewState, lastFileChangeTime);

        var allTests = suites
            .SelectMany(s => s.Tests.Select(t => (Suite: s, Test: t)))
            .Where(x => matcher is null || matcher(x.Test.FullyQualifiedName))
            .Where(x => IsStatusVisible(x.Test.Status, viewState))
            .OrderBy(x => x.Test.DisplayName)
            .ToList();

        if (allTests.Count == 0)
        {
            AddNoResultsNode(tree, null);
            return;
        }

        foreach (var (suite, test) in allTests)
        {
            var isStale  = IsStale(suite, lastFileChangeTime);
            var isPinned = viewState?.PinnedTestFqns.Contains(test.FullyQualifiedName) ?? false;
            tree.AddRootNode(new TreeNode(TestLabel(test, isStale, isPinned)) { Tag = new TestNodeTag.Test(test) });
        }
    }

    // ── Pinned section helper ────────────────────────────────────────────────

    /// <summary>
    /// Inserts a "★ Pinned" root node at the top of the tree containing all pinned tests,
    /// regardless of current filters. Called before other grouping methods.
    /// </summary>
    private static void AddPinnedSection(
        TreeControl tree,
        IReadOnlyList<TestSuite> suites,
        ViewState viewState,
        DateTimeOffset? lastFileChangeTime)
    {
        if (viewState.PinnedTestFqns.Count == 0) return;

        var pinnedTests = suites
            .SelectMany(s => s.Tests.Select(t => (Suite: s, Test: t)))
            .Where(x => viewState.PinnedTestFqns.Contains(x.Test.FullyQualifiedName))
            .OrderBy(x => x.Test.DisplayName)
            .ToList();

        if (pinnedTests.Count == 0) return;

        var pinnedRoot = new TreeNode($"[gold1]★ Pinned[/] [dim]({pinnedTests.Count})[/]")
        {
            Tag = new TestNodeTag.Group("★ Pinned"),
            IsExpanded = true,
        };

        foreach (var (suite, test) in pinnedTests)
        {
            var isStale = IsStale(suite, lastFileChangeTime);
            pinnedRoot.AddChild(new TreeNode(TestLabel(test, isStale, isPinned: true)) { Tag = new TestNodeTag.Test(test) });
        }

        tree.AddRootNode(pinnedRoot);
    }

    // ── Filtering helpers ────────────────────────────────────────────────────

    private static IReadOnlyList<TestResult> GetVisibleTests(
        IReadOnlyList<TestResult> tests,
        Func<string, bool>? matcher,
        ViewState? viewState)
    {
        IEnumerable<TestResult> filtered = tests;
        if (matcher is not null)
            filtered = filtered.Where(t => matcher(t.FullyQualifiedName));
        filtered = filtered.Where(t => IsStatusVisible(t.Status, viewState));
        return filtered.ToList();
    }

    private static bool IsStatusVisible(TestStatus status, ViewState? viewState)
    {
        if (viewState is null) return true;
        return status switch
        {
            TestStatus.Passed  => viewState.ShowPassed,
            TestStatus.Failed  => viewState.ShowFailed,
            TestStatus.Skipped => viewState.ShowSkipped,
            TestStatus.NotRun  => viewState.ShowNotRun,
            TestStatus.Running => true, // always show running
            _                  => true,
        };
    }

    private static bool IsStale(TestSuite suite, DateTimeOffset? lastFileChangeTime) =>
        lastFileChangeTime.HasValue && suite.Timestamp < lastFileChangeTime.Value;

    private static void AddNoResultsNode(TreeControl tree, string? filter)
    {
        var msg = filter is not null
            ? $"No tests match filter: {filter}"
            : "No tests discovered";
        tree.AddRootNode(new TreeNode(msg) { Tag = new TestNodeTag.Group("") });
    }

    // ── Node label helpers ────────────────────────────────────────────────────

    private static string SuiteLabel(TestSuite suite, IEnumerable<TestResult> visibleTests)
    {
        var tests   = visibleTests.ToList();
        var passed  = tests.Count(t => t.Status == TestStatus.Passed);
        var failed  = tests.Count(t => t.Status == TestStatus.Failed);
        var running = tests.Count(t => t.Status == TestStatus.Running);
        var icon    = SuiteIcon(failed, running, tests.Count(t => t.Status == TestStatus.Skipped), tests.Count);
        return $"{icon} {EscapeMarkup(suite.Name)} [dim]({passed}✓ {failed}✗)[/]";
    }

    private static string SuiteIcon(int failed, int running, int skipped, int total)
    {
        if (total == 0)  return "[dim]◌[/]";
        if (failed > 0)  return "[red3]✗[/]";
        if (running > 0) return "[cyan]⟳[/]";
        if (skipped > 0) return "[gold1]●[/]";
        return "[green3]✓[/]";
    }

    private static string NamespaceLabel(string ns, IReadOnlyList<TestResult> tests)
    {
        var failed  = tests.Count(t => t.Status == TestStatus.Failed);
        var running = tests.Count(t => t.Status == TestStatus.Running);
        var icon    = failed > 0 ? "[red3]✗[/]" : running > 0 ? "[cyan]⟳[/]" : "[green3]✓[/]";
        // Use only the last namespace segment for readability
        var simple = ns.Contains('.') ? ns[(ns.LastIndexOf('.') + 1)..] : ns;
        return $"{icon} {EscapeMarkup(simple)}";
    }

    private static string ClassLabel(string classKey, IReadOnlyList<TestResult> classTests)
    {
        var failed  = classTests.Count(t => t.Status == TestStatus.Failed);
        var running = classTests.Count(t => t.Status == TestStatus.Running);
        var icon    = failed > 0 ? "[red3]✗[/]" : running > 0 ? "[cyan]⟳[/]" : "[green3]✓[/]";
        // classKey is the simple class name (last segment before method)
        var simple = classKey.Contains('.') ? classKey[(classKey.LastIndexOf('.') + 1)..] : classKey;
        return $"{icon} {EscapeMarkup(simple)}";
    }

    private static string TestLabel(TestResult test, bool isStale = false, bool isPinned = false)
    {
        var icon = test.Status switch
        {
            TestStatus.Passed  => "[green3]✓[/]",
            TestStatus.Failed  => "[red3]✗[/]",
            TestStatus.Skipped => "[gold1]●[/]",
            TestStatus.Running => "[cyan]⟳[/]",
            _                  => "[dim]◌[/]",
        };
        var pin      = isPinned ? "★ " : "";
        var name     = EscapeMarkup(test.DisplayName);
        var duration = test.Duration > TimeSpan.Zero && test.Status != TestStatus.Running
            ? $" [dim]{FormatDuration(test.Duration)}[/]"
            : "";

        if (isStale)
            return $"[dim]{icon} {pin}{name}{duration}[/]";

        return $"{icon} {pin}{name}{duration}";
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalMilliseconds < 1000
            ? $"{(int)d.TotalMilliseconds}ms"
            : $"{d.TotalSeconds:F1}s";

    private static void AddTestLeaves(
        TreeNode parent,
        IEnumerable<TestResult> tests,
        TestSuite suite,
        ViewState? viewState,
        DateTimeOffset? lastFileChangeTime)
    {
        foreach (var test in tests.OrderBy(t => t.DisplayName))
        {
            var isStale  = IsStale(suite, lastFileChangeTime);
            var isPinned = viewState?.PinnedTestFqns.Contains(test.FullyQualifiedName) ?? false;
            parent.AddChild(new TreeNode(TestLabel(test, isStale, isPinned)) { Tag = new TestNodeTag.Test(test) });
        }
    }

    // ── FQN decomposition ────────────────────────────────────────────────────
    //
    // FQN structure:  Namespace.Parts.ClassName.MethodName
    //   namespace  = everything before the last two dot-segments
    //   classKey   = second-to-last segment (qualified: Namespace.ClassName)
    //   method     = last segment
    //
    // Example: "MyApp.Tests.Integration.MyClass.MyMethod"
    //   namespace = "MyApp.Tests.Integration"
    //   classKey  = "MyApp.Tests.Integration.MyClass"
    //   method    = "MyMethod"

    /// <summary>Returns the namespace portion of a FQN (everything before the last two segments).</summary>
    private static string NamespaceKey(string fqn)
    {
        var parts = fqn.Split('.');
        return parts.Length <= 2
            ? string.Empty
            : string.Join('.', parts[..^2]);
    }

    /// <summary>Returns namespace + class (everything before the last segment).</summary>
    private static string ClassKey(string fqn)
    {
        var idx = fqn.LastIndexOf('.');
        return idx < 0 ? fqn : fqn[..idx];
    }

    // ── Utility ──────────────────────────────────────────────────────────────

    private static string EscapeMarkup(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");

    private static Func<string, bool>? BuildMatcher(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return null;

        try
        {
            var regex = new Regex(filter, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
            return s => regex.IsMatch(s);
        }
        catch (ArgumentException)
        {
            return s => s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }
    }
}
