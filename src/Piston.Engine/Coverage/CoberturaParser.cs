using System.Xml.Linq;

namespace Piston.Engine.Coverage;

/// <summary>
/// Parses Cobertura XML coverage reports produced by coverlet
/// (<c>--collect "XPlat Code Coverage"</c>).
/// </summary>
/// <remarks>
/// Cobertura structure (simplified):
/// <code>
/// &lt;coverage&gt;
///   &lt;sources&gt;&lt;source&gt;/abs/path&lt;/source&gt;&lt;/sources&gt;
///   &lt;packages&gt;
///     &lt;package&gt;
///       &lt;classes&gt;
///         &lt;class filename="relative/file.cs"&gt;
///           &lt;lines&gt;
///             &lt;line number="10" hits="1" /&gt;
///           &lt;/lines&gt;
///         &lt;/class&gt;
///       &lt;/classes&gt;
///     &lt;/package&gt;
///   &lt;/packages&gt;
/// &lt;/coverage&gt;
/// </code>
/// The <c>filename</c> attribute on <c>class</c> elements is relative to the
/// first <c>source</c> entry. All returned paths are normalized to absolute paths.
/// </remarks>
internal sealed class CoberturaParser : ICoberturaParser
{
    public CoberturaReport Parse(string coberturaXmlPath)
    {
        var doc = XDocument.Load(coberturaXmlPath);
        var root = doc.Root ?? throw new InvalidOperationException("Cobertura XML has no root element.");

        // Extract source directories — coverlet emits one <source> per repo root
        var sources = root
            .Element("sources")?
            .Elements("source")
            .Select(s => s.Value.Trim())
            .Where(s => s.Length > 0)
            .ToList() ?? [];

        // Group by file path — multiple <class> elements can map to the same file
        var linesByFile = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var classEl in root.Descendants("class"))
        {
            var filename = classEl.Attribute("filename")?.Value;
            if (string.IsNullOrEmpty(filename)) continue;

            var absolutePath = ResolveFilePath(filename, sources);

            if (!linesByFile.TryGetValue(absolutePath, out var lineMap))
            {
                lineMap = [];
                linesByFile[absolutePath] = lineMap;
            }

            foreach (var lineEl in classEl.Elements("lines").Elements("line"))
            {
                var numberStr = lineEl.Attribute("number")?.Value;
                var hitsStr   = lineEl.Attribute("hits")?.Value;

                if (!int.TryParse(numberStr, out var lineNumber) ||
                    !int.TryParse(hitsStr, out var hits))
                    continue;

                // Accumulate hits for lines that appear in multiple methods/classes
                lineMap.TryGetValue(lineNumber, out var existingHits);
                lineMap[lineNumber] = existingHits + hits;
            }
        }

        var files = linesByFile
            .Select(kvp =>
            {
                var lines = kvp.Value
                    .Select(l => new LineCoverage(l.Key, l.Value))
                    .OrderBy(l => l.LineNumber)
                    .ToList();
                return new FileCoverage(kvp.Key, lines);
            })
            .ToList();

        return new CoberturaReport(files);
    }

    private static string ResolveFilePath(string filename, IReadOnlyList<string> sources)
    {
        // If filename is already absolute, normalize and return it
        if (Path.IsPathRooted(filename))
            return Path.GetFullPath(filename);

        // Try each source directory
        foreach (var source in sources)
        {
            var candidate = Path.GetFullPath(Path.Combine(source, filename));
            if (File.Exists(candidate))
                return candidate;
        }

        // Fall back: combine with first source, or return normalized relative path
        if (sources.Count > 0)
            return Path.GetFullPath(Path.Combine(sources[0], filename));

        return Path.GetFullPath(filename);
    }
}
