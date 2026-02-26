using System.Text.RegularExpressions;
using Piston.Core.Models;
using Piston.ViewModels;
using SharpConsoleUI.Controls;

namespace Piston.Views;

/// <summary>
/// Builds a SharpConsoleUI <see cref="TreeControl"/> node hierarchy from
/// <see cref="TestSuite"/> data: Project → Namespace → Class → Method.
/// Optionally filters by a substring or regex pattern.
/// </summary>
public static class TestTreeBuilder
{
    /// <summary>
    /// Replaces the contents of <paramref name="tree"/> with nodes built from
    /// <paramref name="suites"/>. Safe to call from the async window thread.
    /// </summary>
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

            // Group by namespace, then by class within each namespace
            var byNamespace = visibleTests
                .GroupBy(t => NamespaceKey(t.FullyQualifiedName))
                .OrderBy(g => g.Key);

            foreach (var nsGroup in byNamespace)
            {
                var nsTests = nsGroup.ToList();

                // If namespace is empty, add class nodes directly under the suite
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
                        AddTestLeaves(classNode, classTestList);
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
                        AddTestLeaves(classNode, classTestList);
                        nsNode.AddChild(classNode);
                    }

                    suiteNode.AddChild(nsNode);
                }
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

    // ── Node label helpers ────────────────────────────────────────────────────

    private static string SuiteLabel(TestSuite suite, IEnumerable<TestResult> visibleTests)
    {
        var tests   = visibleTests.ToList();
        var passed  = tests.Count(t => t.Status == TestStatus.Passed);
        var failed  = tests.Count(t => t.Status == TestStatus.Failed);
        var running = tests.Count(t => t.Status == TestStatus.Running);
        var icon    = SuiteIcon(failed, running, tests.Count(t => t.Status == TestStatus.Skipped), tests.Count);
        return $"{icon} {suite.Name} [dim]({passed}✓ {failed}✗)[/]";
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
        return $"{icon} {simple}";
    }

    private static string ClassLabel(string classKey, IReadOnlyList<TestResult> classTests)
    {
        var failed  = classTests.Count(t => t.Status == TestStatus.Failed);
        var running = classTests.Count(t => t.Status == TestStatus.Running);
        var icon    = failed > 0 ? "[red3]✗[/]" : running > 0 ? "[cyan]⟳[/]" : "[green3]✓[/]";
        // classKey is the simple class name (last segment before method)
        var simple = classKey.Contains('.') ? classKey[(classKey.LastIndexOf('.') + 1)..] : classKey;
        return $"{icon} {simple}";
    }

    private static string TestLabel(TestResult test) => test.Status switch
    {
        TestStatus.Passed  => $"[green3]✓[/] {test.DisplayName}",
        TestStatus.Failed  => $"[red3]✗[/] {test.DisplayName}",
        TestStatus.Skipped => $"[gold1]●[/] {test.DisplayName}",
        TestStatus.Running => $"[cyan]⟳[/] {test.DisplayName}",
        _                  => $"[dim]◌[/] {test.DisplayName}",
    };

    private static void AddTestLeaves(TreeNode parent, IEnumerable<TestResult> tests)
    {
        foreach (var test in tests.OrderBy(t => t.DisplayName))
            parent.AddChild(new TreeNode(TestLabel(test)) { Tag = new TestNodeTag.Test(test) });
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
