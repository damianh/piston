using System.Text.RegularExpressions;
using Piston.Core.Models;
using Piston.ViewModels;
using SharpConsoleUI.Controls;

namespace Piston.Views;

/// <summary>
/// Builds a SharpConsoleUI <see cref="TreeControl"/> node hierarchy from
/// <see cref="TestSuite"/> data: Suite → Class → Method.
/// Optionally filters by a substring or regex pattern.
/// </summary>
public static class TestTreeBuilder
{
    /// <summary>
    /// Replaces the contents of <paramref name="tree"/> with nodes built from
    /// <paramref name="suites"/>. Safe to call from the async window thread.
    /// </summary>
    /// <param name="filter">
    /// Optional substring or regex. When non-null, only tests whose
    /// <see cref="TestResult.FullyQualifiedName"/> matches are shown.
    /// </param>
    public static void Rebuild(TreeControl tree, IReadOnlyList<TestSuite> suites, string? filter = null)
    {
        var matcher = BuildMatcher(filter);

        tree.Clear();

        if (suites.Count == 0)
        {
            tree.AddRootNode(new TreeNode("No tests discovered") { Tag = new TestNodeTag.Group("") });
            return;
        }

        var anyVisible = false;

        foreach (var suite in suites)
        {
            // Apply filter: keep only matching tests
            var visibleTests = matcher is null
                ? suite.Tests
                : suite.Tests.Where(t => matcher(t.FullyQualifiedName)).ToList();

            if (visibleTests.Count == 0) continue;
            anyVisible = true;

            var suiteNode = new TreeNode(SuiteLabel(suite, visibleTests))
            {
                Tag = new TestNodeTag.Suite(suite),
                IsExpanded = true,
            };

            var byClass = visibleTests
                .GroupBy(t => ClassKey(t.FullyQualifiedName))
                .OrderBy(g => g.Key);

            foreach (var classGroup in byClass)
            {
                var classTests = classGroup.ToList();
                var classNode = new TreeNode(ClassLabel(classGroup.Key, classTests))
                {
                    Tag = new TestNodeTag.Group(classGroup.Key),
                    IsExpanded = true,
                };

                foreach (var test in classTests.OrderBy(t => t.DisplayName))
                {
                    classNode.AddChild(new TreeNode(TestLabel(test))
                    {
                        Tag = new TestNodeTag.Test(test),
                    });
                }

                suiteNode.AddChild(classNode);
            }

            tree.AddRootNode(suiteNode);
        }

        if (!anyVisible)
        {
            var msg = filter is not null
                ? $"No tests match filter: {filter}"
                : "No tests discovered";
            tree.AddRootNode(new TreeNode(msg) { Tag = new TestNodeTag.Group("") });
        }
    }

    // ── Label helpers ──────────────────────────────────────────────────────────

    private static string SuiteLabel(TestSuite suite, IEnumerable<TestResult> visibleTests)
    {
        var tests   = visibleTests.ToList();
        var passed  = tests.Count(t => t.Status == TestStatus.Passed);
        var failed  = tests.Count(t => t.Status == TestStatus.Failed);
        var icon    = SuiteIcon(failed, tests.Count(t => t.Status == TestStatus.Skipped), tests.Count);
        return $"{icon} {suite.Name} [dim]({passed}✓ {failed}✗)[/]";
    }

    private static string SuiteIcon(int failed, int skipped, int total)
    {
        if (total == 0) return "[dim]◌[/]";
        if (failed > 0) return "[red]✗[/]";
        if (skipped > 0) return "[yellow]●[/]";
        return "[green]✓[/]";
    }

    private static string ClassLabel(string classKey, IReadOnlyList<TestResult> classTests)
    {
        var failed = classTests.Count(t => t.Status == TestStatus.Failed);
        var icon   = failed > 0 ? "[red]✗[/]" : "[green]✓[/]";
        var simpleName = classKey.Contains('.')
            ? classKey[(classKey.LastIndexOf('.') + 1)..]
            : classKey;
        return $"{icon} {simpleName}";
    }

    private static string TestLabel(TestResult test) => test.Status switch
    {
        TestStatus.Passed  => $"[green]✓[/] {test.DisplayName}",
        TestStatus.Failed  => $"[red]✗[/] {test.DisplayName}",
        TestStatus.Skipped => $"[yellow]●[/] {test.DisplayName}",
        TestStatus.Running => $"[cyan]⟳[/] {test.DisplayName}",
        _                  => $"[dim]◌[/] {test.DisplayName}",
    };

    // ── Utility ────────────────────────────────────────────────────────────────

    /// <summary>Returns everything before the last dot in a FQN.</summary>
    private static string ClassKey(string fqn)
    {
        var idx = fqn.LastIndexOf('.');
        return idx < 0 ? fqn : fqn[..idx];
    }

    /// <summary>
    /// Builds a predicate for <paramref name="filter"/>.
    /// Tries to compile it as a regex; falls back to case-insensitive substring if it's not valid regex.
    /// Returns null when filter is null/empty.
    /// </summary>
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
            // Not a valid regex — use substring match
            return s => s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }
    }
}
