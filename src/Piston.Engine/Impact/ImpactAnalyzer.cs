// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Impact;

internal sealed class ImpactAnalyzer : IImpactAnalyzer
{
    private readonly Func<string, ISolutionGraph> _graphFactory;
    private readonly ICoverageStore? _coverageStore;
    private ISolutionGraph? _graph;
    private string? _solutionPath;
    private readonly object _graphLock = new();

    public ImpactAnalyzer(Func<string, ISolutionGraph> graphFactory) => _graphFactory = graphFactory;

    public ImpactAnalyzer(Func<string, ISolutionGraph> graphFactory, ICoverageStore? coverageStore)
    {
        _graphFactory = graphFactory;
        _coverageStore = coverageStore;
    }

    public async Task InitializeAsync(string solutionPath, CancellationToken ct) => await Task.Run(() =>
                                                                                         {
                                                                                             lock (_graphLock)
                                                                                             {
                                                                                                 _solutionPath = solutionPath;
                                                                                                 RefreshGraph(solutionPath);
                                                                                             }
                                                                                         }, ct).ConfigureAwait(false);

    // Reevaluate rather than trusting the impact graph cached before the agent's latest edit.
    public string? GetVerificationCoverageIssue(string solutionPath) =>
        GetVerificationCoverageIssue(solutionPath, out _);

    public string? GetVerificationCoverageIssue(string solutionPath, out IReadOnlyList<string> testProjects)
    {
        lock (_graphLock)
        {
            RefreshGraph(solutionPath);
            testProjects = _graph!.TestProjectPaths.ToArray();
            return _graph!.VerificationCoverageIssue;
        }
    }

    public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes) => Analyze(changes, record: true);

    public ImpactAnalysisResult Preview(IReadOnlyList<FileChangeEvent> changes) => Analyze(changes, record: false);

    private ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes, bool record)
    {
        lock (_graphLock)
        {
            var graphRefreshed = false;
            if (record && _solutionPath is not null && changes.Any(c =>
                Path.GetExtension(c.FilePath).ToLowerInvariant() is
                    ".sln" or ".slnx" or ".slnf" or ".csproj" or ".props" or ".targets"))
            {
                graphRefreshed = TryRefreshGraph(_solutionPath);
            }

            var result = AnalyzeCore(changes, record);
            return graphRefreshed ? result with { RequiresGraphRebuild = false } : result;
        }
    }

    private ImpactAnalysisResult AnalyzeCore(IReadOnlyList<FileChangeEvent> changes, bool record)
    {
        // If graph is not available, fall back to full run
        if (_graph is null)
        {
            return FullRunResult() with { Explanation = "The solution graph is not loaded." };
        }

        // Check for solution/project file changes that require graph rebuild
        var hasSolutionChange = changes.Any(c =>
            c.FilePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
            c.FilePath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
            c.FilePath.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase));

        if (hasSolutionChange)
        {
            return FullRunResult() with { Explanation = "A solution file changed." };
        }

        var graph = _graph;
        var affectedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var affectedTestProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requiresGraphRebuild = false;
        string? unknownFile = null;
        var hasTestFileChange = false;

        foreach (var change in changes)
        {
            var ext = Path.GetExtension(change.FilePath);

            if (ext.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".targets", StringComparison.OrdinalIgnoreCase))
            {
                requiresGraphRebuild = true;
                // A props/targets change potentially affects everything — full run
                return FullRunResult() with
                {
                    RequiresGraphRebuild = true,
                    Explanation = $"{Path.GetFileName(change.FilePath)} can affect every project.",
                };
            }

            if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                requiresGraphRebuild = true;
                var projectPath = Path.GetFullPath(change.FilePath);
                _ = affectedProjects.Add(projectPath);

                foreach (var dep in graph.GetTransitiveDependents(projectPath))
                {
                    _ = affectedProjects.Add(dep);
                }

                continue;
            }

            // For .cs files: use Tier 1 (directory walking) then Tier 2 (graph)
            var owningProject = FindOwningProjectByHeuristic(change.FilePath) ?? graph.FindOwningProject(change.FilePath);

            if (owningProject is null)
            {
                unknownFile ??= change.FilePath;
                continue;
            }

            if (graph.IsTestProject(owningProject))
            {
                // A test file change: rebuild and run only that test project
                _ = affectedTestProjects.Add(owningProject);
                hasTestFileChange |= change.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                _ = affectedProjects.Add(owningProject);
                foreach (var dep in graph.GetTransitiveDependents(owningProject))
                {
                    _ = affectedProjects.Add(dep);
                }
            }
        }

        if (unknownFile is not null)
        {
            return FullRunResult() with { Explanation = $"{unknownFile} is not in a known project." };
        }

        if (affectedProjects.Count == 0 && affectedTestProjects.Count == 0)
        {
            return FullRunResult() with { Explanation = "No project owns the changed files." };
        }

        // From affected projects (non-test), find test projects that depend on them
        foreach (var proj in affectedProjects.ToList())
        {
            if (graph.IsTestProject(proj))
            {
                _ = affectedTestProjects.Add(proj);
            }
        }

        // Non-test affected projects (build targets)
        var buildProjects = affectedProjects
            .Where(p => !graph.IsTestProject(p))
            .ToList();

        var tier2Result = new ImpactAnalysisResult(
            AffectedProjectPaths: buildProjects,
            AffectedTestProjectPaths: [.. affectedTestProjects],
            RequiresGraphRebuild: requiresGraphRebuild,
            IsFullRun: false
        )
        {
            Explanation = "Tests are selected by project.",
        };

        // ── Tier 3: coverage-based test-level narrowing ────────────────────────
        if (_coverageStore is null)
        {
            return tier2Result with { Explanation = "Coverage is disabled, so tests are selected by project." };
        }

        var csPaths = changes
            .Where(c => c.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(c => Path.GetFullPath(c.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (csPaths.Count == 0)
        {
            return tier2Result with { Explanation = "No C# source changed, so tests are selected by project." };
        }

        ImpactAnalysisResult? tier3Result = null;
        var abstained = hasTestFileChange
            ? "A test file changed and coverage cannot know its new tests, so tests are selected by project."
            : null;

        // An edited test file may add tests no recording knows about, so only Tier 2 is safe.
        if (_coverageStore is IImpactMapStore impactMap)
        {
            var changed = csPaths.Select(p => new ChangedFile(p, ImpactContent.TryRead(p))).ToList();
            var selection = impactMap.SelectTests(changed);
            if (selection.UnmappedFiles.Count == 0
                && selection.Tests.Count == 0
                && selection.UnchangedFiles.Count == changed.Count)
            {
                tier3Result = tier2Result with
                {
                    AffectedTestProjectPaths = [],
                    AffectedTestFqns = [],
                    Explanation = "Only comments or whitespace changed, so no tests need to run.",
                };
            }
            else if (hasTestFileChange)
            {
                abstained = "A test file changed and coverage cannot know its new tests, so tests are selected by project.";
            }
            else if (selection.UnmappedFiles.Count == 0 && selection.Tests.Count > 0)
            {
                tier3Result = tier2Result with
                {
                    AffectedTestFqns = [.. selection.Tests.Keys.Order(StringComparer.Ordinal)],
                    SelectionReasons = selection.Tests.ToDictionary(
                        kv => kv.Key,
                        kv => (IReadOnlyList<TestSelectionReason>)[.. kv.Value.Select(ToSelectionReason)],
                        StringComparer.Ordinal),
                    Explanation = "Tests whose recorded execution touches the changed code.",
                };
            }
            else if (selection.UnmappedFiles.Count > 0)
            {
                abstained = $"No coverage recording includes {string.Join(", ", selection.UnmappedFiles.Select(Path.GetFileName))}, so tests are selected by project.";
            }

            if (record)
            {
                try
                {
                    // Class and test recordings of the old content no longer describe the file.
                    var unchanged = selection.UnchangedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    impactMap.MarkChangedAsync([.. changed.Where(file => !unchanged.Contains(file.FilePath))]).GetAwaiter().GetResult();
                }
                catch
                {
                    // Impact map bookkeeping is best effort; the run-level map still selects.
                }
            }
        }

        var tier3Fqns = new HashSet<string>(StringComparer.Ordinal);
        var allHaveCoverage = !hasTestFileChange;
        foreach (var path in csPaths)
        {
            if (!_coverageStore.HasCoverageData(path))
            {
                allHaveCoverage = false;
            }
            else
            {
                tier3Fqns.UnionWith(_coverageStore.GetTestsCoveringFile(path));
            }

            if (record)
            {
                // Mark stale after reading: prevents the next run from using
                // this coverage until it has been refreshed by a new test run
                _ = _coverageStore.MarkFileStaleAsync(path);
            }
        }

        if (tier3Result is not null)
        {
            return tier3Result;
        }

        if (allHaveCoverage && tier3Fqns.Count > 0)
        {
            // File-level coverage narrowed the run — attach the FQN list
            return tier2Result with
            {
                AffectedTestFqns = [.. tier3Fqns],
                Explanation = "Tests whose coverage includes the changed files.",
            };
        }

        return tier2Result with
        {
            Explanation = abstained ?? "Coverage has not measured every changed file, so tests are selected by project.",
        };
    }

    public ImpactAnalysisResult AnalyzeFullRun()
    {
        lock (_graphLock)
        {
            if (_solutionPath is not null)
            {
                _ = TryRefreshGraph(_solutionPath);
            }
            return FullRunResult() with { Explanation = "A full run was requested." };
        }
    }

    public void InvalidateGraph()
    {
        lock (_graphLock)
        {
            _graph = null;
        }
    }

    private void RefreshGraph(string solutionPath)
    {
        // A failed refresh must not leave a stale graph available for run selection.
        _graph = null;
        _graph = _graphFactory(solutionPath);
    }

    private bool TryRefreshGraph(string solutionPath)
    {
        try
        {
            RefreshGraph(solutionPath);
            return true;
        }
        catch
        {
            // Graph load failure is non-fatal for ordinary runs: use the solution target.
            return false;
        }
    }

    public bool RefreshGraphIfEvaluatedBeforeRestore()
    {
        lock (_graphLock)
        {
            return _graph is { EvaluatedBeforeRestore: true } && _solutionPath is not null && TryRefreshGraph(_solutionPath);
        }
    }

    public IReadOnlyList<string> GetAllTestProjectPaths()
    {
        lock (_graphLock)
        {
            return _graph?.TestProjectPaths ?? [];
        }
    }

    public IReadOnlyList<string>? GetRestoredTestProjectPaths()
    {
        lock (_graphLock)
        {
            return _graph is { EvaluatedBeforeRestore: false } ? _graph.TestProjectPaths : null;
        }
    }

    public IReadOnlyList<string> GetBuildProjectPaths(IReadOnlyList<string>? buildTargets)
    {
        lock (_graphLock)
        {
            if (_graph is null)
            {
                return [];
            }

            if (buildTargets is null || buildTargets.Count == 0)
            {
                return _graph.AllProjectPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in buildTargets)
            {
                _ = projects.Add(Path.GetFullPath(target));
                projects.UnionWith(_graph.GetTransitiveDependencies(target));
            }

            return [.. projects];
        }
    }

    public bool IsMtpProject(string projectPath)
    {
        lock (_graphLock)
        {
            return _graph?.IsMtpProject(projectPath) ?? false;
        }
    }

    public string? GetMtpOutputPath(string projectPath)
    {
        lock (_graphLock)
        {
            return _graph?.GetMtpOutputPath(projectPath);
        }
    }

    public TestProjectFacts? GetTestProjectFacts(string projectPath)
    {
        lock (_graphLock)
        {
            return _graph?.GetTestProjectFacts(projectPath);
        }
    }

    public bool IsMultiTargeted(string projectPath)
    {
        lock (_graphLock)
        {
            return _graph?.IsMultiTargeted(projectPath) ?? false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TestSelectionReason ToSelectionReason(ImpactReason reason) => new(
        reason.FilePath,
        reason.ChangedLines.ToString(),
        reason.ExecutedSpan?.ToString(),
        reason.Granularity.ToString().ToLowerInvariant(),
        reason.ProjectPath,
        reason.Member,
        reason.Branches is null
            ? null
            : [.. reason.Branches.Select(b => new BranchEvidence(b.Line, b.Covered, b.Total))]);

    private static ImpactAnalysisResult FullRunResult() =>
        new([], [], RequiresGraphRebuild: false, IsFullRun: true);

    /// <summary>
    /// Tier 1: Walk up the directory tree from <paramref name="filePath"/> looking
    /// for a <c>.csproj</c> file. Returns null if none is found or the directory does not exist.
    /// Returns a normalized full path.
    /// </summary>
    private static string? FindOwningProjectByHeuristic(string filePath)
    {
        string? dir;
        try
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        }
        catch
        {
            return null;
        }

        while (dir is not null)
        {
            try
            {
                var csproj = Directory.EnumerateFiles(dir, "*.csproj").FirstOrDefault();
                if (csproj is not null)
                {
                    return Path.GetFullPath(csproj);
                }
            }
            catch
            {
                return null;
            }

            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
