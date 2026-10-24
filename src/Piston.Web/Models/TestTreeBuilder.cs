// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.RegularExpressions;
using Piston.Protocol.Dtos;
using Piston.Protocol.Messages;

namespace Piston.Web.Models;

/// <summary>
/// Builds a <see cref="TestNode"/> hierarchy from <see cref="TestSuiteDto"/> data.
/// Supports multiple grouping strategies, status filters, pinned tests, and stale-result dimming.
/// </summary>
public static class TestTreeBuilder
{
    /// <summary>
    /// Suites to show while tests run: the live overlay first, then last-known results for tests
    /// outside this run (e.g. projects untouched by a selective run).
    /// </summary>
    public static IReadOnlyList<TestSuiteDto> WithLiveOverlay(
        IReadOnlyList<TestSuiteDto> suites,
        IReadOnlyList<TestSuiteDto> inProgressSuites)
    {
        if (inProgressSuites.Count == 0)
        {
            return suites;
        }

        var live = inProgressSuites
            .SelectMany(s => s.Tests)
            .Select(t => t.FullyQualifiedName)
            .ToHashSet(StringComparer.Ordinal);

        var untouched = suites
            .Select(s => s with { Tests = s.Tests.Where(t => !live.Contains(t.FullyQualifiedName)).ToList() })
            .Where(s => s.Tests.Count > 0);

        return [.. inProgressSuites, .. untouched];
    }

    /// <summary>
    /// True until a build has completed and the run that follows has finished discovering tests,
    /// so an empty list reads as "still discovering" rather than "nothing found".
    /// </summary>
    public static bool IsDiscovering(StateSnapshotNotification snapshot) =>
        snapshot.Phase is PistonPhaseDto.Analyzing or PistonPhaseDto.Building or PistonPhaseDto.Testing
        || (snapshot.LastBuild is null && snapshot.Phase is not PistonPhaseDto.Error);

    public static List<TestNode> Build(
        IReadOnlyList<TestSuiteDto> suites,
        string? filter,
        ViewState viewState,
        DateTimeOffset? lastFileChangeTime,
        bool discovering = false)
    {
        var matcher = BuildMatcher(filter);
        var roots = new List<TestNode>();

        if (suites.Count == 0)
        {
            roots.Add(new TestNode { Label = discovering ? "Discovering tests…" : "No tests discovered", Kind = TestNodeKind.Group, GroupKey = "" });
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

        SortNodes(roots, viewState, preserveOrder: viewState.Grouping == GroupingMode.ByStatus);
        return roots;
    }

    private static void SortNodes(List<TestNode> nodes, ViewState viewState, bool preserveOrder = false)
    {
        foreach (var node in nodes)
        {
            SortNodes(node.Children, viewState);
        }

        if (preserveOrder || nodes.Count < 2)
        {
            return;
        }

        var entries = nodes.Select(n => (Node: n, Duration: viewState.TestSort == TestSort.Duration ? n.Counts().DurationMs : 0));
        var ordered = entries.OrderBy(x => x.Node.Structure == NodeStructure.Pinned ? 0 : 1);
        if (viewState.TestSort == TestSort.Duration)
        {
            ordered = viewState.TestSortDescending
                ? ordered.ThenByDescending(x => x.Duration)
                : ordered.ThenBy(x => x.Duration);
        }

        var byName = Comparer<TestNode>.Create((a, b) =>
            a.Structure == NodeStructure.Framework && b.Structure == NodeStructure.Framework
                ? FrameworkComparer.Instance.Compare(a.Label, b.Label)
                : StringComparer.Ordinal.Compare(a.Label, b.Label));
        ordered = viewState.TestSort == TestSort.Name && viewState.TestSortDescending
            ? ordered.ThenByDescending(x => x.Node, byName)
            : ordered.ThenBy(x => x.Node, byName);
        var sorted = ordered.Select(x => x.Node).ToList();
        nodes.Clear();
        nodes.AddRange(sorted);
    }

    /// <summary>
    /// The project's results as one suite, or null when it has none. A selective run adds a suite for the
    /// tests it reran and keeps the project's other results in the previous one, so a project can have several;
    /// the latest supplies the timestamp and run id.
    /// </summary>
    public static TestSuiteDto? ForProject(IEnumerable<TestSuiteDto> suites, string project)
    {
        var parts = suites.Where(s => string.Equals(s.Name, project, StringComparison.Ordinal)).ToList();
        return parts.Count == 0 ? null : Combine(parts);
    }

    private static TestSuiteDto Combine(IReadOnlyList<TestSuiteDto> parts) =>
        parts.Count == 1
            ? parts[0]
            : parts.MaxBy(s => s.Timestamp)! with
            {
                Tests = [.. parts.SelectMany(s => s.Tests)],
                TotalDurationMs = parts.Sum(s => s.TotalDurationMs),
            };

    private static void BuildByProjectNsClass(
        List<TestNode> roots,
        IReadOnlyList<TestSuiteDto> suites,
        Func<string, bool>? matcher,
        ViewState viewState,
        DateTimeOffset? lastFileChangeTime)
    {
        AddPinnedSection(roots, suites, viewState, lastFileChangeTime);

        var anyVisible = false;

        foreach (var parts in suites.GroupBy(s => s.Name, StringComparer.Ordinal))
        {
            var suite = Combine([.. parts]);
            var visibleTests = GetVisibleTests(suite.Tests, suite.Name, matcher, viewState);
            if (visibleTests.Count == 0)
            {
                continue;
            }

            anyVisible = true;

            // Each part keeps its own staleness: results a selective run did not refresh can predate a change.
            var staleTests = parts
                .Where(p => IsStale(p, lastFileChangeTime))
                .SelectMany(p => p.Tests)
                .ToHashSet<TestResultDto>(ReferenceEqualityComparer.Instance);
            bool isStale(TestResultDto t) => staleTests.Contains(t);

            var suiteNode = new TestNode
            {
                Label = suite.Name,
                Kind = TestNodeKind.Suite,
                Suite = suite,
                ExpansionKey = $"suite:{suite.Name}",
                IsExpanded = viewState.IsNodeExpanded($"suite:{suite.Name}"),
            };

            // A multi-targeted project runs each test once per framework: group by framework first, so a
            // failure under one framework stays visible when the tree is collapsed.
            var byFramework = visibleTests
                .Where(t => t.TargetFramework is not null)
                .GroupBy(t => t.TargetFramework!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, FrameworkComparer.Instance)
                .ToList();

            if (byFramework.Count == 0)
            {
                AddNamespaceTree(suiteNode.Children, visibleTests, isStale, suite.Name, suite.Name, viewState);
            }
            else
            {
                foreach (var frameworkGroup in byFramework)
                {
                    var scope = $"{suite.Name}|{frameworkGroup.Key}";
                    var frameworkNode = new TestNode
                    {
                        Label = frameworkGroup.Key,
                        Kind = TestNodeKind.Group,
                        GroupKey = frameworkGroup.Key,
                        ExpansionKey = $"tfm:{scope}",
                        IsExpanded = viewState.IsNodeExpanded($"tfm:{scope}"),
                    };
                    AddNamespaceTree(frameworkNode.Children, [.. frameworkGroup], isStale, scope, suite.Name, viewState);
                    suiteNode.Children.Add(frameworkNode);
                }

                // Tests discovered but not yet run have no framework until their results arrive.
                AddNamespaceTree(
                    suiteNode.Children, [.. visibleTests.Where(t => t.TargetFramework is null)], isStale, suite.Name, suite.Name, viewState);
            }

            roots.Add(suiteNode);
        }

        if (!anyVisible)
        {
            roots.Add(new TestNode { Label = "No tests match current filters", Kind = TestNodeKind.Group, GroupKey = "" });
        }
    }

    /// <summary>Adds namespace and class nodes for <paramref name="tests"/>; <paramref name="scope"/> keeps their expansion keys unique.</summary>
    private static void AddNamespaceTree(
        List<TestNode> parent,
        IReadOnlyList<TestResultDto> tests,
        Func<TestResultDto, bool> isStale,
        string scope,
        string project,
        ViewState viewState)
    {
        var byNamespace = tests
            .GroupBy(t => NamespaceKey(t.FullyQualifiedName))
            .OrderBy(g => g.Key);

        foreach (var nsGroup in byNamespace)
        {
            var classGroups = nsGroup
                .GroupBy(t => ClassKey(t.FullyQualifiedName))
                .OrderBy(g => g.Key)
                .Select(g => (Key: g.Key, Tests: g.ToList()));

            if (string.IsNullOrEmpty(nsGroup.Key))
            {
                foreach (var (classKey, classTests) in classGroups)
                {
                    // Dot-less names (e.g. a custom DisplayName) have no class to group under.
                    if (classTests.All(t => t.FullyQualifiedName == classKey))
                    {
                        AddTestLeaves(parent, classTests, isStale, project);
                        continue;
                    }

                    parent.Add(ClassNode(classKey, classTests, isStale, scope, project, viewState));
                }
            }
            else
            {
                var nsNode = new TestNode
                {
                    Label = SimpleSegment(nsGroup.Key),
                    Kind = TestNodeKind.Group,
                    GroupKey = nsGroup.Key,
                    ExpansionKey = $"ns:{scope}|{nsGroup.Key}",
                    IsExpanded = viewState.IsNodeExpanded($"ns:{scope}|{nsGroup.Key}"),
                };

                foreach (var (classKey, classTests) in classGroups)
                {
                    nsNode.Children.Add(ClassNode(classKey, classTests, isStale, scope, project, viewState));
                }

                parent.Add(nsNode);
            }
        }
    }

    private static TestNode ClassNode(
        string classKey,
        List<TestResultDto> classTests,
        Func<TestResultDto, bool> isStale,
        string scope,
        string project,
        ViewState viewState)
    {
        var classNode = new TestNode
        {
            Label = SimpleSegment(classKey),
            Kind = TestNodeKind.Group,
            GroupKey = classKey,
            ExpansionKey = $"class:{scope}|{classKey}",
            IsExpanded = viewState.IsNodeExpanded($"class:{scope}|{classKey}"),
        };
        AddTestLeaves(classNode.Children, classTests, isStale, project);
        return classNode;
    }

    /// <summary>Orders target frameworks by version where they parse (net8.0 before net10.0), else by name.</summary>
    private sealed class FrameworkComparer : IComparer<string?>
    {
        public static readonly FrameworkComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var (xFamily, xVersion) = Parse(x ?? string.Empty);
            var (yFamily, yVersion) = Parse(y ?? string.Empty);
            var byFamily = StringComparer.OrdinalIgnoreCase.Compare(xFamily, yFamily);
            if (byFamily != 0)
            {
                return byFamily;
            }

            var byVersion = Comparer<Version?>.Default.Compare(xVersion, yVersion);
            return byVersion != 0 ? byVersion : StringComparer.OrdinalIgnoreCase.Compare(x, y);
        }

        // "net8.0-windows" -> ("net", 8.0); "netstandard2.0" -> ("netstandard", 2.0); "net481" -> ("net", 481).
        private static (string Family, Version? Version) Parse(string tfm)
        {
            var head = tfm.Split('-')[0];
            var digit = head.IndexOfAny("0123456789".ToCharArray());
            if (digit < 0)
            {
                return (head, null);
            }

            var number = head[digit..];
            return (head[..digit], Version.TryParse(number.Contains('.') ? number : number + ".0", out var v) ? v : null);
        }
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
            .Where(x => IsHistoryVisible(x.Test, x.Suite.Name, viewState))
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
            if (!show)
            {
                continue;
            }

            var grouped = allTests.Where(x => x.Test.Status == status).ToList();
            if (grouped.Count == 0)
            {
                continue;
            }

            var groupNode = new TestNode
            {
                Label = $"{label} ({grouped.Count})",
                Kind = TestNodeKind.Group,
                GroupKey = label,
                ExpansionKey = $"status:{label}",
                IsExpanded = viewState.IsNodeExpanded($"status:{label}"),
            };

            foreach (var (suite, test) in OrderTests(grouped, x => x.Test, ClassMethodLabel)
                .ThenBy(x => x.Test.TargetFramework, FrameworkComparer.Instance))
            {
                var isStale = IsStale(suite, lastFileChangeTime);
                groupNode.Children.Add(new TestNode
                {
                    Label = ClassMethodLabel(test.FullyQualifiedName),
                    Kind = TestNodeKind.Test,
                    Test = test,
                    Project = suite.Name,
                    IsStale = isStale,
                    Framework = test.TargetFramework,
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
            .Where(x => IsStatusVisible(x.Test.Status, viewState) && IsHistoryVisible(x.Test, x.Suite.Name, viewState));
        var ordered = OrderTests(allTests, x => x.Test, ClassMethodLabel)
            .ThenBy(x => x.Test.TargetFramework, FrameworkComparer.Instance)
            .ToList();

        if (ordered.Count == 0)
        {
            roots.Add(new TestNode { Label = "No tests match current filters", Kind = TestNodeKind.Group, GroupKey = "" });
            return;
        }

        foreach (var (suite, test) in ordered)
        {
            var isStale = IsStale(suite, lastFileChangeTime);
            roots.Add(new TestNode
            {
                Label = ClassMethodLabel(test.FullyQualifiedName),
                Kind = TestNodeKind.Test,
                Test = test,
                Project = suite.Name,
                IsStale = isStale,
                Framework = test.TargetFramework,
            });
        }
    }

    private static void AddPinnedSection(
        List<TestNode> roots,
        IReadOnlyList<TestSuiteDto> suites,
        ViewState viewState,
        DateTimeOffset? lastFileChangeTime)
    {
        if (viewState.PinnedTestFqns.Count == 0)
        {
            return;
        }

        var pinnedTests = suites
            .SelectMany(s => s.Tests.Select(t => (Suite: s, Test: t)))
            .Where(x => viewState.PinnedTestFqns.Contains(x.Test.FullyQualifiedName))
            .OrderBy(x => ClassMethodLabel(x.Test.FullyQualifiedName), StringComparer.Ordinal)
            .ThenBy(x => x.Test.TargetFramework, FrameworkComparer.Instance)
            .ToList();

        if (pinnedTests.Count == 0)
        {
            return;
        }

        var pinnedRoot = new TestNode
        {
            Label = $"★ Pinned ({pinnedTests.Count})",
            Kind = TestNodeKind.Group,
            GroupKey = "★ Pinned",
            ExpansionKey = "pinned",
            IsExpanded = viewState.IsNodeExpanded("pinned"),
        };

        foreach (var (suite, test) in pinnedTests)
        {
            var isStale = IsStale(suite, lastFileChangeTime);
            pinnedRoot.Children.Add(new TestNode
            {
                Label = ClassMethodLabel(test.FullyQualifiedName),
                Kind = TestNodeKind.Test,
                Test = test,
                Project = suite.Name,
                IsStale = isStale,
                Framework = test.TargetFramework,
            });
        }

        roots.Add(pinnedRoot);
    }

    private static IOrderedEnumerable<T> OrderTests<T>(
        IEnumerable<T> items, Func<T, TestResultDto> test, Func<string, string> label) =>
        items.OrderBy(x => label(test(x).FullyQualifiedName), StringComparer.Ordinal);

    private static void AddTestLeaves(
        List<TestNode> parent,
        IEnumerable<TestResultDto> tests,
        Func<TestResultDto, bool> isStale,
        string project)
    {
        foreach (var test in OrderTests(tests, t => t, MethodLabel))
        {
            parent.Add(new TestNode
            {
                Label = MethodLabel(test.FullyQualifiedName),
                Kind = TestNodeKind.Test,
                Test = test,
                Project = project,
                IsStale = isStale(test),
            });
        }
    }

    private static IReadOnlyList<TestResultDto> GetVisibleTests(
        IReadOnlyList<TestResultDto> tests,
        string project,
        Func<string, bool>? matcher,
        ViewState viewState)
    {
        IEnumerable<TestResultDto> filtered = tests;
        if (matcher is not null)
        {
            filtered = filtered.Where(t => matcher(t.FullyQualifiedName));
        }

        filtered = filtered.Where(t => IsStatusVisible(t.Status, viewState) && IsHistoryVisible(t, project, viewState));
        return filtered.ToList();
    }

    /// <summary>True when the test is a new failure, for <see cref="ViewState.OnlyNewFailures"/>.</summary>
    public static bool IsNewFailure(TestResultDto test, string? project, ViewState viewState) =>
        test.Status == TestStatusDto.Failed
        && viewState.GetTestMark(project, test.Key)?.FailureKind == "New";

    /// <summary>True when the run history marks the test flaky.</summary>
    public static bool IsFlaky(TestResultDto test, string? project, ViewState viewState) =>
        viewState.GetTestMark(project, test.Key)?.Flaky == true;

    // With both history filters on, a test passes either.
    private static bool IsHistoryVisible(TestResultDto test, string project, ViewState viewState) =>
        !(viewState.OnlyNewFailures || viewState.OnlyFlaky)
        || (viewState.OnlyNewFailures && IsNewFailure(test, project, viewState))
        || (viewState.OnlyFlaky && IsFlaky(test, project, viewState));

    private static bool IsStatusVisible(TestStatusDto status, ViewState viewState) =>
        status switch
        {
            TestStatusDto.Passed => viewState.ShowPassed,
            TestStatusDto.Failed => viewState.ShowFailed,
            TestStatusDto.Skipped => viewState.ShowSkipped,
            TestStatusDto.NotRun => viewState.ShowNotRun,
            _ => true,
        };

    private static bool IsStale(TestSuiteDto suite, DateTimeOffset? lastFileChangeTime) =>
        lastFileChangeTime.HasValue && suite.Timestamp < lastFileChangeTime.Value;

    // Dots inside a parameterised test's argument list (e.g. "dotnet.exe") are not name separators.
    internal static string NameHead(string fqn)
    {
        var paren = fqn.IndexOf('(');
        return paren < 0 ? fqn : fqn[..paren];
    }

    private static string NamespaceKey(string fqn)
    {
        var parts = NameHead(fqn).Split('.');
        return parts.Length <= 2 ? string.Empty : string.Join('.', parts[..^2]);
    }

    internal static string ClassKey(string fqn)
    {
        var idx = NameHead(fqn).LastIndexOf('.');
        return idx < 0 ? fqn : fqn[..idx];
    }

    /// <summary>Method name (with any arguments) for leaves that sit under their class node.</summary>
    internal static string MethodLabel(string fqn)
    {
        var idx = NameHead(fqn).LastIndexOf('.');
        return idx < 0 ? fqn : fqn[(idx + 1)..];
    }

    /// <summary>Class.Method label for lists where the class node is not shown.</summary>
    internal static string ClassMethodLabel(string fqn)
    {
        var classKey = ClassKey(fqn);
        return classKey == fqn ? fqn : $"{SimpleSegment(classKey)}.{MethodLabel(fqn)}";
    }

    /// <summary>
    /// Detail-pane heading and secondary line. The default display name (the FQN or the bare method) becomes the
    /// method label over its class; a custom display name keeps the FQN beneath it so the test can be located.
    /// </summary>
    internal static (string Title, string? Subtitle) DetailNames(TestResultDto test)
    {
        var fqn = test.FullyQualifiedName;
        var method = MethodLabel(fqn);
        var custom = !string.IsNullOrWhiteSpace(test.DisplayName) && test.DisplayName != fqn && test.DisplayName != method;
        if (custom)
        {
            return (test.DisplayName, fqn);
        }

        var classKey = ClassKey(fqn);
        return (method, classKey == fqn ? null : classKey);
    }

    internal static string SimpleSegment(string key)
    {
        var idx = key.LastIndexOf('.');
        return idx < 0 ? key : key[(idx + 1)..];
    }

    private static Func<string, bool>? BuildMatcher(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

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
