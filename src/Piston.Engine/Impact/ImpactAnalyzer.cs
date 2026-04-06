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
    private bool _rebuildScheduled;

    public ImpactAnalyzer(Func<string, ISolutionGraph> graphFactory)
    {
        _graphFactory = graphFactory;
    }

    public ImpactAnalyzer(Func<string, ISolutionGraph> graphFactory, ICoverageStore? coverageStore)
    {
        _graphFactory  = graphFactory;
        _coverageStore = coverageStore;
    }

    public async Task InitializeAsync(string solutionPath, CancellationToken ct)
    {
        _solutionPath = solutionPath;
        _graph = await Task.Run(() => _graphFactory(solutionPath), ct).ConfigureAwait(false);
        _rebuildScheduled = false;
    }

    public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes)
    {
        // If a rebuild was scheduled from a prior run, execute it now synchronously
        // (we're already on a background analysis thread)
        if (_rebuildScheduled && _solutionPath is not null)
        {
            try
            {
                _graph = _graphFactory(_solutionPath);
            }
            catch
            {
                _graph = null;
            }
            _rebuildScheduled = false;
        }

        // If graph is not available, fall back to full run
        if (_graph is null)
            return FullRunResult();

        // Check for solution/project file changes that require graph rebuild
        var hasSolutionChange = changes.Any(c =>
            c.FilePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
            c.FilePath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
            c.FilePath.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase));

        if (hasSolutionChange)
        {
            _rebuildScheduled = true;
            return FullRunResult();
        }

        var graph = _graph;
        var affectedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var affectedTestProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requiresGraphRebuild = false;
        var hasUnknownFile = false;

        foreach (var change in changes)
        {
            var ext = Path.GetExtension(change.FilePath);

            if (ext.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".targets", StringComparison.OrdinalIgnoreCase))
            {
                requiresGraphRebuild = true;
                // A props/targets change potentially affects everything — full run
                return FullRunResult() with { RequiresGraphRebuild = true };
            }

            if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                requiresGraphRebuild = true;
                var projectPath = Path.GetFullPath(change.FilePath);
                affectedProjects.Add(projectPath);

                foreach (var dep in graph.GetTransitiveDependents(projectPath))
                    affectedProjects.Add(dep);

                continue;
            }

            // For .cs files: use Tier 1 (directory walking) then Tier 2 (graph)
            var owningProject = FindOwningProjectByHeuristic(change.FilePath) ?? graph.FindOwningProject(change.FilePath);

            if (owningProject is null)
            {
                hasUnknownFile = true;
                continue;
            }

            if (graph.IsTestProject(owningProject))
            {
                // A test file change: just run that test project (no source build needed beyond itself)
                affectedTestProjects.Add(owningProject);
            }
            else
            {
                affectedProjects.Add(owningProject);
                foreach (var dep in graph.GetTransitiveDependents(owningProject))
                    affectedProjects.Add(dep);
            }
        }

        if (hasUnknownFile)
            return FullRunResult();

        if (affectedProjects.Count == 0 && affectedTestProjects.Count == 0)
            return FullRunResult();

        // From affected projects (non-test), find test projects that depend on them
        foreach (var proj in affectedProjects.ToList())
        {
            if (graph.IsTestProject(proj))
                affectedTestProjects.Add(proj);
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
        );

        // ── Tier 3: coverage-based test-level narrowing ────────────────────────
        if (_coverageStore is not null)
        {
            var csChanges = changes
                .Where(c => c.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (csChanges.Count > 0)
            {
                var tier3Fqns    = new HashSet<string>(StringComparer.Ordinal);
                var allHaveCoverage = true;

                foreach (var change in csChanges)
                {
                    var normalizedPath = Path.GetFullPath(change.FilePath);

                    if (!_coverageStore.HasCoverageData(normalizedPath))
                    {
                        allHaveCoverage = false;
                        // Still mark stale so future runs don't use this file's old coverage
                        _ = _coverageStore.MarkFileStaleAsync(normalizedPath);
                    }
                    else
                    {
                        var tests = _coverageStore.GetTestsCoveringFile(normalizedPath);
                        foreach (var t in tests)
                            tier3Fqns.Add(t);

                        // Mark stale after reading: prevents the next run from using
                        // this coverage until it has been refreshed by a new test run
                        _ = _coverageStore.MarkFileStaleAsync(normalizedPath);
                    }
                }

                if (allHaveCoverage && tier3Fqns.Count > 0)
                {
                    // Tier 3 narrowed the run — attach the FQN list
                    return tier2Result with { AffectedTestFqns = [.. tier3Fqns] };
                }
            }
        }

        return tier2Result;
    }

    public ImpactAnalysisResult AnalyzeFullRun() => FullRunResult();

    public void InvalidateGraph()
    {
        _graph = null;
        _rebuildScheduled = false;
    }

    public IReadOnlyList<string> GetAllTestProjectPaths() =>
        _graph?.TestProjectPaths ?? [];

    public bool IsMtpProject(string projectPath) =>
        _graph?.IsMtpProject(projectPath) ?? false;

    public string? GetMtpOutputPath(string projectPath) =>
        _graph?.GetMtpOutputPath(projectPath);

    // ── Helpers ───────────────────────────────────────────────────────────────

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
                    return Path.GetFullPath(csproj);
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
