using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Impact;

internal sealed class ImpactAnalyzer : IImpactAnalyzer
{
    private readonly Func<string, ISolutionGraph> _graphFactory;
    private ISolutionGraph? _graph;
    private string? _solutionPath;
    private bool _rebuildScheduled;

    public ImpactAnalyzer(Func<string, ISolutionGraph> graphFactory)
    {
        _graphFactory = graphFactory;
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
            c.FilePath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));

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

        return new ImpactAnalysisResult(
            AffectedProjectPaths: buildProjects,
            AffectedTestProjectPaths: [.. affectedTestProjects],
            RequiresGraphRebuild: requiresGraphRebuild,
            IsFullRun: false
        );
    }

    public ImpactAnalysisResult AnalyzeFullRun() => FullRunResult();

    public void InvalidateGraph()
    {
        _graph = null;
        _rebuildScheduled = false;
    }

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
