using System.Text.RegularExpressions;
using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>
/// Builds a <see cref="TestNode"/> hierarchy from <see cref="TestSuiteDto"/> data.
/// Supports multiple grouping strategies, status filters, pinned tests, and stale-result dimming.
/// </summary>
public static class TestTreeBuilder
{
    public static List<TestNode> Build(
        IReadOnlyList<TestSuiteDto> suites,
        string? filter,
        ViewState viewState,
        DateTimeOffset? lastFileChangeTime)
    {
        var matcher = BuildMatcher(filter);
        var roots = new List<TestNode>();

        if (suites.Count == 0)
        {
            roots.Add(new TestNode { Label = "No tests discovered", Kind = TestNodeKind.Group, GroupKey = "" });
            return roots;
        }

        switch (viewState.Grouping)
        {
            case GroupingMode.ByStatus:
                BuildByStatus(roots, suites, matcher, viewState, lastFileChangeTime);
                break;
            case GroupingMode.Flat:
                BuildFlat(roots, suites, matcher, viewState, lastFileChangeTime);
                break;
            default:
                BuildByProjectNsClass(roots, suites, matcher, viewState, lastFileChangeTime);
                break;
        }

        return roots;
    }

    private static void BuildByProjectNsClass(
        List<TestNode> roots,
        IReadOnlyList<TestSuiteDto> suites,
        Func<string, bool>? matcher,
        ViewState viewState,
        DateTimeOffset? lastFileChangeTime)
    {
        AddPinnedSection(roots, suites, viewState, lastFileChangeTime);

        var anyVisible = false;

        foreach (var suite in suites)
        {
            var visibleTests = GetVisibleTests(suite.Tests, matcher, viewState);
            if (visibleTests.Count == 0) continue;
            anyVisible = true;

            var suiteNode = new TestNode
            {
                Label = suite.Name,
                Kind = TestNodeKind.Suite,
                Suite = suite,
                IsExpanded = viewState.TreeExpanded,
            };

            var byNamespace = visibleTests
                .GroupBy(t => NamespaceKey(t.FullyQualifiedName))
                .OrderBy(g => g.Key);

            foreach (var nsGroup in byNamespace)
            {
                var nsTests = nsGroup.ToList();
                var classGroups = nsGroup
                    .GroupBy(t => ClassKey(t.FullyQualifiedName))
                    .OrderBy(g => g.Key)
                    .Select(g => (Key: g.Key, Tests: g.ToList()));

                if (string.IsNullOrEmpty(nsGroup.Key))
                {
                    foreach (var (classKey, classTests) in classGroups)
                    {
                        var classNode = new TestNode
                        {
                            Label = SimpleSegment(classKey),
                            Kind = TestNodeKind.Group,
                            GroupKey = classKey,
                            IsExpanded = viewState.TreeExpanded,
                        };
                        AddTestLeaves(classNode.Children, classTests, suite, viewState, lastFileChangeTime);
                        suiteNode.Children.Add(classNode);
                    }
                }
                else
                {
                    var nsNode = new TestNode
                    {
                        Label = SimpleSegment(nsGroup.Key),
                        Kind = TestNodeKind.Group,
                        GroupKey = nsGroup.Key,
                        IsExpanded = viewState.TreeExpanded,
                    };

                    foreach (var (classKey, classTests) in classGroups)
                    {
                        var classNode = new TestNode
                        {
                            Label = SimpleSegment(classKey),
                            Kind = TestNodeKind.Group,
                            GroupKey = classKey,
                            IsExpanded = viewState.TreeExpanded,
                        };
                        AddTestLeaves(classNode.Children, classTests, suite, viewState, lastFileChangeTime);
                        nsNode.Children.Add(classNode);
                    }

                    suiteNode.Children.Add(nsNode);
                }
            }

            roots.Add(suiteNode);
        }

        if (!anyVisible)
            roots.Add(new TestNode { Label = "No tests match current filters", Kind = TestNodeKind.Group, GroupKey = "" });
    }

    private static void BuildByStatus(
        List<TestNode> roots,
        IReadOnlyList<TestSuiteDto> suites,
        Func<string, bool>? matcher,
        ViewState viewState,
        DateTimeOffset? lastFileChangeTime)
    {
        AddPinnedSection(roots, suites, viewState, lastFileChangeTime);

        var allTests = suites
            .SelectMany(s => s.Tests.Select(t => (Suite: s, Test: t)))
            .Where(x => matcher is null || matcher(x.Test.FullyQualifiedName))
            .ToList();

        if (allTests.Count == 0)
        {
            roots.Add(new TestNode { Label = "No tests match current filters", Kind = TestNodeKind.Group, GroupKey = "" });
            return;
        }

        var groups = new[]
        {
            ("✗ Failed",  TestStatusDto.Failed,  viewState.ShowFailed),
            ("⟳ Running", TestStatusDto.Running, true),
            ("✓ Passed",  TestStatusDto.Passed,  viewState.ShowPassed),
            ("● Skipped", TestStatusDto.Skipped, viewState.ShowSkipped),
            ("◌ Not Run", TestStatusDto.NotRun,  viewState.ShowNotRun),
        };

        foreach (var (label, status, show) in groups)
        {
            if (!show) continue;
            var grouped = allTests.Where(x => x.Test.Status == status).ToList();
            if (grouped.Count == 0) continue;

            var groupNode = new TestNode
            {
                Label = $"{label} ({grouped.Count})",
                Kind = TestNodeKind.Group,
                GroupKey = label,
                IsExpanded = viewState.TreeExpanded,
            };

            foreach (var (suite, test) in grouped.OrderBy(x => x.Test.DisplayName))
            {
                var isStale = IsStale(suite, lastFileChangeTime);
                groupNode.Children.Add(new TestNode
                {
                    Label = test.DisplayName,
                    Kind = TestNodeKind.Test,
                    Test = test,
                    IsStale = isStale,
                });
            }

            roots.Add(groupNode);
        }
    }

    private static void BuildFlat(
        List<TestNode> roots,
        IReadOnlyList<TestSuiteDto> suites,
        Func<string, bool>? matcher,
        ViewState viewState,
        DateTimeOffset? lastFileChangeTime)
    {
        AddPinnedSection(roots, suites, viewState, lastFileChangeTime);

        var allTests = suites
            .SelectMany(s => s.Tests.Select(t => (Suite: s, Test: t)))
            .Where(x => matcher is null || matcher(x.Test.FullyQualifiedName))
            .Where(x => IsStatusVisible(x.Test.Status, viewState))
            .OrderBy(x => x.Test.DisplayName)
            .ToList();

        if (allTests.Count == 0)
        {
            roots.Add(new TestNode { Label = "No tests match current filters", Kind = TestNodeKind.Group, GroupKey = "" });
            return;
        }

        foreach (var (suite, test) in allTests)
        {
            var isStale = IsStale(suite, lastFileChangeTime);
            roots.Add(new TestNode
            {
                Label = test.DisplayName,
                Kind = TestNodeKind.Test,
                Test = test,
                IsStale = isStale,
            });
        }
    }

    private static void AddPinnedSection(
        List<TestNode> roots,
        IReadOnlyList<TestSuiteDto> suites,
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

        var pinnedRoot = new TestNode
        {
            Label = $"★ Pinned ({pinnedTests.Count})",
            Kind = TestNodeKind.Group,
            GroupKey = "★ Pinned",
            IsExpanded = true,
        };

        foreach (var (suite, test) in pinnedTests)
        {
            var isStale = IsStale(suite, lastFileChangeTime);
            pinnedRoot.Children.Add(new TestNode
            {
                Label = test.DisplayName,
                Kind = TestNodeKind.Test,
                Test = test,
                IsStale = isStale,
            });
        }

        roots.Add(pinnedRoot);
    }

    private static void AddTestLeaves(
        List<TestNode> parent,
        IEnumerable<TestResultDto> tests,
        TestSuiteDto suite,
        ViewState viewState,
        DateTimeOffset? lastFileChangeTime)
    {
        foreach (var test in tests.OrderBy(t => t.DisplayName))
        {
            var isStale = IsStale(suite, lastFileChangeTime);
            parent.Add(new TestNode
            {
                Label = test.DisplayName,
                Kind = TestNodeKind.Test,
                Test = test,
                IsStale = isStale,
            });
        }
    }

    private static IReadOnlyList<TestResultDto> GetVisibleTests(
        IReadOnlyList<TestResultDto> tests,
        Func<string, bool>? matcher,
        ViewState viewState)
    {
        IEnumerable<TestResultDto> filtered = tests;
        if (matcher is not null)
            filtered = filtered.Where(t => matcher(t.FullyQualifiedName));
        filtered = filtered.Where(t => IsStatusVisible(t.Status, viewState));
        return filtered.ToList();
    }

    private static bool IsStatusVisible(TestStatusDto status, ViewState viewState) =>
        status switch
        {
            TestStatusDto.Passed  => viewState.ShowPassed,
            TestStatusDto.Failed  => viewState.ShowFailed,
            TestStatusDto.Skipped => viewState.ShowSkipped,
            TestStatusDto.NotRun  => viewState.ShowNotRun,
            _                     => true,
        };

    private static bool IsStale(TestSuiteDto suite, DateTimeOffset? lastFileChangeTime) =>
        lastFileChangeTime.HasValue && suite.Timestamp < lastFileChangeTime.Value;

    private static string NamespaceKey(string fqn)
    {
        var parts = fqn.Split('.');
        return parts.Length <= 2 ? string.Empty : string.Join('.', parts[..^2]);
    }

    private static string ClassKey(string fqn)
    {
        var idx = fqn.LastIndexOf('.');
        return idx < 0 ? fqn : fqn[..idx];
    }

    private static string SimpleSegment(string key)
    {
        var idx = key.LastIndexOf('.');
        return idx < 0 ? key : key[(idx + 1)..];
    }

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
