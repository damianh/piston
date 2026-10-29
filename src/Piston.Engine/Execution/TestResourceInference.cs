// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Piston.Engine.History;
using Piston.Engine.Impact;

namespace Piston.Engine.Execution;

/// <summary>
/// Decides which exclusive resources each test project's run holds. Projects that reference a container
/// testing package (Testcontainers, Aspire.Hosting.Testing, …) are inferred to use containers and, unless
/// configured otherwise, are isolated per repository: one run of the project at a time across all of the
/// repository's worktrees, since such tests commonly reuse fixed container names and ports.
/// <c>testResources</c> rules in <c>.piston.json</c> declare further exclusive resources or relax the default.
/// </summary>
public sealed class TestResourceInference
{
    private readonly string _solutionDirectory;
    private readonly IReadOnlyList<CompiledRule> _rules;
    private readonly Func<string, TestProjectFacts?> _facts;
    private readonly Lazy<(string Root, string RepositoryId)> _repository;

    public TestResourceInference(
        string solutionPath, IReadOnlyList<TestResourceRule> rules, Func<string, TestProjectFacts?> facts)
    {
        _solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? Path.GetFullPath(solutionPath);
        _rules = [.. rules.Select(Compile)];
        _facts = facts;
        _repository = new Lazy<(string, string)>(() => ResolveRepository(solutionPath));
    }

    /// <summary>Whether a package reference marks a project as using containers.</summary>
    public static bool IsContainerPackage(string package) =>
        package.Equals("Testcontainers", StringComparison.OrdinalIgnoreCase)
        || package.StartsWith("Testcontainers.", StringComparison.OrdinalIgnoreCase)
        || package.StartsWith("DotNet.Testcontainers", StringComparison.OrdinalIgnoreCase)
        || package.Equals("Aspire.Hosting.Testing", StringComparison.OrdinalIgnoreCase)
        || package.StartsWith("Docker.DotNet", StringComparison.OrdinalIgnoreCase)
        || package.StartsWith("Ductus.FluentDocker", StringComparison.OrdinalIgnoreCase)
        || package.StartsWith("Squadron.", StringComparison.OrdinalIgnoreCase);

    public TestResourceNeeds Resolve(string projectPath)
    {
        var reasons = new List<string>();
        var keys = new SortedSet<string>(StringComparer.Ordinal);

        var containerPackages = _facts(projectPath)?.PackageReferences
            .Where(IsContainerPackage)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        var usesContainers = containerPackages.Count > 0;
        if (usesContainers)
        {
            reasons.Add($"references {string.Join(", ", containerPackages)}");
        }

        TestResourceIsolation? isolation = null;
        var relative = RelativeToSolution(projectPath);
        foreach (var rule in _rules.Where(r => r.Matches(relative)))
        {
            var set = new List<string>();
            if (rule.Rule.Containers is { } containers)
            {
                usesContainers = containers;
                set.Add($"containers: {(containers ? "true" : "false")}");
            }

            if (rule.Rule.Isolation is { } configured)
            {
                isolation = configured;
                set.Add($"isolation: {configured.ToString().ToLowerInvariant()}");
            }

            keys.UnionWith(rule.Keys);
            if (rule.Keys.Count > 0)
            {
                set.Add($"exclusive: {string.Join(", ", rule.Keys)}");
            }

            reasons.Add($"testResources '{rule.Rule.Projects}'{(set.Count > 0 ? ": " + string.Join("; ", set) : "")}");
        }

        if ((isolation ?? (usesContainers ? TestResourceIsolation.Repository : TestResourceIsolation.None))
            == TestResourceIsolation.Repository)
        {
            _ = keys.Add(ProjectKey(projectPath));
        }

        return new TestResourceNeeds(projectPath, [.. keys], usesContainers, reasons);
    }

    /// <summary>The <c>projects</c> patterns of rules that match none of <paramref name="projectPaths"/>.</summary>
    public IReadOnlyList<string> UnmatchedRules(IEnumerable<string> projectPaths)
    {
        var relative = projectPaths.Select(RelativeToSolution).ToList();
        return [.. _rules.Where(r => !relative.Any(r.Matches)).Select(r => r.Rule.Projects)];
    }

    /// <summary>
    /// <c>project:&lt;repository&gt;/&lt;path&gt;</c>: the same for the project in every worktree of the repository.
    /// </summary>
    internal string ProjectKey(string projectPath)
    {
        var (root, repositoryId) = _repository.Value;
        string path;
        try
        {
            path = Path.GetRelativePath(root, WorktreeIdentity.CanonicalizePath(projectPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExecutionCoordinationException)
        {
            path = Path.GetRelativePath(root, Path.GetFullPath(projectPath));
        }

        return $"{TestResourceKey.ProjectPrefix}{repositoryId}/{path.Replace('\\', '/')}";
    }

    private string RelativeToSolution(string projectPath) =>
        Path.GetRelativePath(_solutionDirectory, Path.GetFullPath(projectPath)).Replace('\\', '/');

    private static (string Root, string RepositoryId) ResolveRepository(string solutionPath)
    {
        string root;
        try
        {
            root = WorktreeIdentity.CanonicalizePath(WorktreeIdentity.FromSolutionPath(solutionPath).RootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExecutionCoordinationException)
        {
            root = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? solutionPath;
        }

        // Outside git the worktree root identifies the "repository", so only that checkout is isolated.
        var repository = HistoryLocation.FindRepository(root) ?? root;
        var label = Path.GetFileName(repository) is ".git" ? Path.GetFileName(Path.GetDirectoryName(repository)) : Path.GetFileName(repository);
        if (label?.EndsWith(".git", StringComparison.OrdinalIgnoreCase) == true)
        {
            label = label[..^4];
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(repository)))[..12];
        var safeLabel = new string([.. (string.IsNullOrEmpty(label) ? "repository" : label)
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]);
        return (root, $"{safeLabel}-{hash}");
    }

    private static CompiledRule Compile(TestResourceRule rule)
    {
        var keys = new List<string>();
        foreach (var raw in rule.Exclusive)
        {
            if (!TestResourceKey.TryNormalize(raw, out var key, out var error))
            {
                throw new ArgumentException($"Invalid exclusive resource in testResources '{rule.Projects}': {error}", nameof(rule));
            }

            keys.Add(key);
        }

        return new CompiledRule(rule, [.. keys.Distinct(StringComparer.Ordinal)], GlobToRegex(rule.Projects));
    }

    /// <summary>
    /// <c>**</c> matches any number of path segments, <c>*</c> and <c>?</c> stay within a segment. Patterns
    /// without <c>/</c> match the file name. Matching ignores case.
    /// </summary>
    internal static Regex GlobToRegex(string pattern)
    {
        var glob = pattern.Trim().Replace('\\', '/');
        if (glob.StartsWith("./", StringComparison.Ordinal))
        {
            glob = glob[2..];
        }

        if (!glob.Contains('/'))
        {
            glob = "**/" + glob;
        }

        var regex = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                {
                    i++;
                    _ = regex.Append("(?:.*/)?");
                }
                else
                {
                    _ = regex.Append(".*");
                }
            }
            else if (c == '*')
            {
                _ = regex.Append("[^/]*");
            }
            else if (c == '?')
            {
                _ = regex.Append("[^/]");
            }
            else
            {
                _ = regex.Append(Regex.Escape(c.ToString()));
            }
        }

        return new Regex(regex.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private sealed record CompiledRule(TestResourceRule Rule, IReadOnlyList<string> Keys, Regex Glob)
    {
        public bool Matches(string relativePath) => Glob.IsMatch(relativePath);
    }
}
