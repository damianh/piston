// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Xml.Linq;
using Piston.Engine.Impact;

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
///             &lt;line number="10" hits="1" branch="True" condition-coverage="50% (1/2)" /&gt;
///           &lt;/lines&gt;
///         &lt;/class&gt;
///       &lt;/classes&gt;
///     &lt;/package&gt;
///   &lt;/packages&gt;
/// &lt;/coverage&gt;
/// </code>
/// The <c>filename</c> attribute on <c>class</c> elements is relative to the
/// first <c>source</c> entry. All returned paths are normalized to absolute paths.
/// The enclosing <c>package</c> name (the assembly) is reported as the file's module.
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
        var linesByFile = new Dictionary<string, Dictionary<int, LineCoverage>>(StringComparer.OrdinalIgnoreCase);
        var moduleByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var spansByFile = new Dictionary<string, List<LineSpan>>(StringComparer.OrdinalIgnoreCase);

        foreach (var classEl in root.Descendants("class"))
        {
            var filename = classEl.Attribute("filename")?.Value;
            if (string.IsNullOrEmpty(filename))
            {
                continue;
            }

            var absolutePath = ResolveFilePath(filename, sources);

            if (!linesByFile.TryGetValue(absolutePath, out var lineMap))
            {
                lineMap = [];
                linesByFile[absolutePath] = lineMap;
            }

            if (!moduleByFile.ContainsKey(absolutePath)
                && classEl.Ancestors("package").FirstOrDefault()?.Attribute("name")?.Value is { Length: > 0 } module)
            {
                moduleByFile[absolutePath] = module;
            }

            // A method any test entered contributes its whole line range, so an edit to a line the tests
            // skipped (an untaken branch) still counts as touching code they executed.
            foreach (var methodEl in classEl.Elements("methods").Elements("method"))
            {
                int? first = null, last = null;
                var executed = false;
                foreach (var lineEl in methodEl.Elements("lines").Elements("line"))
                {
                    if (!int.TryParse(lineEl.Attribute("number")?.Value, out var number))
                    {
                        continue;
                    }

                    first = first is null ? number : Math.Min(first.Value, number);
                    last = last is null ? number : Math.Max(last.Value, number);
                    executed |= int.TryParse(lineEl.Attribute("hits")?.Value, out var methodHits) && methodHits > 0;
                }

                if (executed)
                {
                    if (!spansByFile.TryGetValue(absolutePath, out var spans))
                    {
                        spansByFile[absolutePath] = spans = [];
                    }

                    spans.Add(new LineSpan(first!.Value, last!.Value));
                }
            }

            foreach (var lineEl in classEl.Elements("lines").Elements("line"))
            {
                var numberStr = lineEl.Attribute("number")?.Value;
                var hitsStr = lineEl.Attribute("hits")?.Value;

                if (!int.TryParse(numberStr, out var lineNumber) ||
                    !int.TryParse(hitsStr, out var hits))
                {
                    continue;
                }

                var (branchesCovered, branchesTotal) = ParseConditionCoverage(lineEl.Attribute("condition-coverage")?.Value);

                // Accumulate hits for lines that appear in multiple methods/classes
                lineMap[lineNumber] = lineMap.TryGetValue(lineNumber, out var existing)
                    ? existing with
                    {
                        HitCount = existing.HitCount + hits,
                        BranchesCovered = Math.Max(existing.BranchesCovered, branchesCovered),
                        BranchesTotal = Math.Max(existing.BranchesTotal, branchesTotal),
                    }
                    : new LineCoverage(lineNumber, hits, branchesCovered, branchesTotal);
            }
        }

        var files = linesByFile
            .Select(kvp =>
            {
                var lines = kvp.Value.Values
                    .OrderBy(l => l.LineNumber)
                    .ToList();
                return new FileCoverage(kvp.Key, lines, moduleByFile.GetValueOrDefault(kvp.Key))
                {
                    ExecutedSpans = spansByFile.TryGetValue(kvp.Key, out var spans) ? LineSpan.Normalize(spans) : null,
                };
            })
            .ToList();

        return new CoberturaReport(files);
    }

    /// <summary>Parses <c>condition-coverage="50% (1/2)"</c> into (covered, total).</summary>
    internal static (int Covered, int Total) ParseConditionCoverage(string? value)
    {
        if (value is null)
        {
            return (0, 0);
        }

        var open = value.IndexOf('(', StringComparison.Ordinal);
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var close = value.IndexOf(')', StringComparison.Ordinal);
        if (open < 0 || slash < open || close < slash
            || !int.TryParse(value.AsSpan(open + 1, slash - open - 1), out var covered)
            || !int.TryParse(value.AsSpan(slash + 1, close - slash - 1), out var total)
            || total <= 0 || covered < 0)
        {
            return (0, 0);
        }

        return (Math.Min(covered, total), total);
    }

    private static string ResolveFilePath(string filename, IReadOnlyList<string> sources)
    {
        // If filename is already absolute, normalize and return it
        if (Path.IsPathRooted(filename))
        {
            return Path.GetFullPath(filename);
        }

        // Try each source directory
        foreach (var source in sources)
        {
            var candidate = Path.GetFullPath(Path.Combine(source, filename));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Fall back: combine with first source, or return normalized relative path
        if (sources.Count > 0)
        {
            return Path.GetFullPath(Path.Combine(sources[0], filename));
        }

        return Path.GetFullPath(filename);
    }
}
