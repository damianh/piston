using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Piston.Engine.Services;

/// <summary>
/// Creates temporary solution filter (<c>.slnf</c>) files so a subset of a solution's
/// projects can be built with a single <c>dotnet build</c> invocation. Projects referenced
/// by filtered projects are still built through their <c>ProjectReference</c>s.
/// </summary>
internal static class SolutionFilterBuilder
{
    private static readonly Regex SlnProjectLine = new(
        @"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""(?<path>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Resolves the underlying <c>.sln</c>/<c>.slnx</c> and its member projects (absolute paths).
    /// For a <c>.slnf</c>, members are the projects included by the filter.
    /// Returns null when the solution format is unsupported or cannot be read.
    /// </summary>
    public static (string SolutionPath, IReadOnlySet<string> Projects)? ReadSolution(string solutionPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(solutionPath);
            if (!File.Exists(fullPath))
                return null;

            var ext = Path.GetExtension(fullPath);
            if (ext.Equals(".slnf", StringComparison.OrdinalIgnoreCase))
                return ReadFilter(fullPath);

            var dir = Path.GetDirectoryName(fullPath)!;
            IEnumerable<string> relative;
            if (ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                relative = XDocument.Load(fullPath)
                    .Descendants()
                    .Where(e => e.Name.LocalName == "Project")
                    .Select(e => (string?)e.Attribute("Path"))
                    .OfType<string>();
            }
            else if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                relative = SlnProjectLine.Matches(File.ReadAllText(fullPath))
                    .Select(m => m.Groups["path"].Value)
                    .Where(p => Path.GetExtension(p).EndsWith("proj", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                return null;
            }

            return (fullPath, ToFullPathSet(dir, relative));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a temporary <c>.slnf</c> (outside the watched tree) selecting <paramref name="projects"/>
    /// from <paramref name="underlyingSolutionPath"/>. The caller must delete the returned file.
    /// </summary>
    public static string WriteTemporaryFilter(string underlyingSolutionPath, IEnumerable<string> projects)
    {
        var solutionDir = Path.GetDirectoryName(underlyingSolutionPath)!;
        var dir = Directory.CreateTempSubdirectory("piston-build-").FullName;
        var filterPath = Path.Combine(dir, "selective.slnf");

        using (var stream = File.Create(filterPath))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("solution");
            writer.WriteString("path", underlyingSolutionPath);
            writer.WriteStartArray("projects");
            foreach (var project in projects)
                writer.WriteStringValue(Path.GetRelativePath(solutionDir, project));
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return filterPath;
    }

    private static (string, IReadOnlySet<string>)? ReadFilter(string filterPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(filterPath));
        var solution = doc.RootElement.GetProperty("solution");
        var slnRelative = NormalizeSeparators(solution.GetProperty("path").GetString()!);
        var slnPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(filterPath)!, slnRelative));
        if (!File.Exists(slnPath))
            return null;

        var relative = solution.TryGetProperty("projects", out var projects)
            ? projects.EnumerateArray().Select(p => p.GetString()).OfType<string>()
            : [];

        return (slnPath, ToFullPathSet(Path.GetDirectoryName(slnPath)!, relative));
    }

    private static HashSet<string> ToFullPathSet(string baseDir, IEnumerable<string> relativePaths) =>
        relativePaths
            .Select(p => Path.GetFullPath(Path.Combine(baseDir, NormalizeSeparators(p))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeSeparators(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
}
