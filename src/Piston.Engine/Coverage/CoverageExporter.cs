// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;
using System.Xml;

namespace Piston.Engine.Coverage;

/// <summary>Formats the solution-wide coverage report can be exported in.</summary>
public enum CoverageExportFormat
{
    /// <summary>LCOV tracefile (<c>lcov.info</c>), read by most editors and coverage services.</summary>
    Lcov,

    /// <summary>Cobertura XML, read by CI systems and ReportGenerator.</summary>
    Cobertura,
}

/// <summary>An exported coverage report.</summary>
/// <param name="Content">The report text.</param>
/// <param name="FileCount">Source files in the report.</param>
/// <param name="StaleFilesSkipped">Files left out because they changed after they were measured.</param>
public sealed record CoverageExport(string Content, int FileCount, int StaleFilesSkipped);

/// <summary>
/// Writes the stored coverage report as LCOV or Cobertura. Stale files are left out: their line numbers
/// refer to an older version of the source.
/// </summary>
public static class CoverageExporter
{
    /// <summary>Parses a format name (<c>lcov</c> or <c>cobertura</c>, case-insensitive).</summary>
    public static bool TryParseFormat(string? value, out CoverageExportFormat format)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "lcov" or "lcov.info" or "info":
                format = CoverageExportFormat.Lcov;
                return true;
            case "cobertura" or "xml" or "cobertura.xml":
                format = CoverageExportFormat.Cobertura;
                return true;
            default:
                format = default;
                return false;
        }
    }

    /// <summary>The conventional file name for <paramref name="format"/>.</summary>
    public static string FileName(CoverageExportFormat format) =>
        format == CoverageExportFormat.Lcov ? "lcov.info" : "cobertura.xml";

    /// <summary>The MIME type for <paramref name="format"/>.</summary>
    public static string ContentType(CoverageExportFormat format) =>
        format == CoverageExportFormat.Lcov ? "text/plain; charset=utf-8" : "application/xml; charset=utf-8";

    /// <summary>Exports <paramref name="engine"/>'s coverage report, or returns null when coverage is disabled.</summary>
    public static CoverageExport? Export(IEngine engine, CoverageExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (engine.GetCoverageReport() is not { } report)
        {
            return null;
        }

        var root = engine.State.SolutionPath is { } solution ? Path.GetDirectoryName(Path.GetFullPath(solution)) : null;
        return Export(format, report, engine.GetFileCoverage, root);
    }

    /// <summary>Exports <paramref name="report"/>, reading each file's lines through <paramref name="getFile"/>.</summary>
    /// <param name="sourceRoot">Directory file names are made relative to in Cobertura output; LCOV uses absolute paths.</param>
    public static CoverageExport Export(
        CoverageExportFormat format,
        CoverageReportSnapshot report,
        Func<string, FileCoverageSnapshot?> getFile,
        string? sourceRoot)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(getFile);

        var files = new List<FileCoverageSnapshot>();
        var stale = 0;
        foreach (var summary in report.Files.OrderBy(f => f.FilePath, StringComparer.Ordinal))
        {
            if (summary.IsStale)
            {
                stale++;
                continue;
            }

            if (getFile(summary.FilePath) is { File.IsStale: false } file)
            {
                files.Add(file);
            }
            else
            {
                stale++;
            }
        }

        var content = format == CoverageExportFormat.Lcov
            ? WriteLcov(files)
            : WriteCobertura(files, sourceRoot, report.GeneratedAt);
        return new CoverageExport(content, files.Count, stale);
    }

    private static string WriteLcov(IEnumerable<FileCoverageSnapshot> files)
    {
        var sb = new StringBuilder();
        foreach (var file in files)
        {
            var lines = file.Lines.OrderBy(l => l.LineNumber).ToList();
            _ = sb.Append("TN:\n").Append("SF:").Append(file.File.FilePath).Append('\n');

            int branchesFound = 0, branchesHit = 0;
            foreach (var line in lines.Where(l => l.BranchesTotal > 0))
            {
                // Cobertura keeps only per-line branch counts, so the first BranchesCovered outcomes are reported taken.
                for (var i = 0; i < line.BranchesTotal; i++)
                {
                    var taken = i < line.BranchesCovered ? "1" : "0";
                    _ = sb.Append(CultureInfo.InvariantCulture, $"BRDA:{line.LineNumber},0,{i},{taken}\n");
                }

                branchesFound += line.BranchesTotal;
                branchesHit += Math.Min(line.BranchesCovered, line.BranchesTotal);
            }

            if (branchesFound > 0)
            {
                _ = sb.Append(CultureInfo.InvariantCulture, $"BRF:{branchesFound}\nBRH:{branchesHit}\n");
            }

            foreach (var line in lines)
            {
                _ = sb.Append(CultureInfo.InvariantCulture, $"DA:{line.LineNumber},{line.HitCount}\n");
            }

            _ = sb.Append(CultureInfo.InvariantCulture, $"LF:{lines.Count}\nLH:{lines.Count(l => l.HitCount > 0)}\n")
                .Append("end_of_record\n");
        }

        return sb.ToString();
    }

    private static string WriteCobertura(IReadOnlyList<FileCoverageSnapshot> files, string? sourceRoot, DateTimeOffset? generatedAt)
    {
        var all = files.SelectMany(f => f.Lines).ToList();
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("coverage");
            WriteRates(writer, all);
            writer.WriteAttributeString("lines-covered", Invariant(all.Count(l => l.HitCount > 0)));
            writer.WriteAttributeString("lines-valid", Invariant(all.Count));
            writer.WriteAttributeString("branches-covered", Invariant(all.Sum(l => Math.Min(l.BranchesCovered, l.BranchesTotal))));
            writer.WriteAttributeString("branches-valid", Invariant(all.Sum(l => l.BranchesTotal)));
            writer.WriteAttributeString("complexity", "0");
            writer.WriteAttributeString("version", "1.9");
            writer.WriteAttributeString("timestamp", Invariant((generatedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()));

            writer.WriteStartElement("sources");
            if (!string.IsNullOrEmpty(sourceRoot))
            {
                writer.WriteElementString("source", sourceRoot);
            }

            writer.WriteEndElement();

            writer.WriteStartElement("packages");
            foreach (var module in files.GroupBy(f => f.File.Module ?? "(unknown)").OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                writer.WriteStartElement("package");
                writer.WriteAttributeString("name", module.Key);
                WriteRates(writer, module.SelectMany(f => f.Lines).ToList());
                writer.WriteAttributeString("complexity", "0");
                writer.WriteStartElement("classes");
                foreach (var file in module)
                {
                    var name = RelativeName(file.File.FilePath, sourceRoot);
                    writer.WriteStartElement("class");
                    writer.WriteAttributeString("name", name);
                    writer.WriteAttributeString("filename", name);
                    WriteRates(writer, file.Lines);
                    writer.WriteAttributeString("complexity", "0");
                    writer.WriteStartElement("methods");
                    writer.WriteEndElement();
                    writer.WriteStartElement("lines");
                    foreach (var line in file.Lines.OrderBy(l => l.LineNumber))
                    {
                        writer.WriteStartElement("line");
                        writer.WriteAttributeString("number", Invariant(line.LineNumber));
                        writer.WriteAttributeString("hits", Invariant(line.HitCount));
                        writer.WriteAttributeString("branch", line.BranchesTotal > 0 ? "true" : "false");
                        if (line.BranchesTotal > 0)
                        {
                            var covered = Math.Min(line.BranchesCovered, line.BranchesTotal);
                            var percent = (int)Math.Round(100.0 * covered / line.BranchesTotal);
                            writer.WriteAttributeString(
                                "condition-coverage",
                                string.Create(CultureInfo.InvariantCulture, $"{percent}% ({covered}/{line.BranchesTotal})"));
                        }

                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRates(XmlWriter writer, IReadOnlyCollection<LineCoverage> lines)
    {
        var branches = lines.Sum(l => l.BranchesTotal);
        var lineRate = lines.Count == 0 ? 1.0 : (double)lines.Count(l => l.HitCount > 0) / lines.Count;
        var branchRate = branches == 0 ? 1.0 : (double)lines.Sum(l => Math.Min(l.BranchesCovered, l.BranchesTotal)) / branches;
        writer.WriteAttributeString("line-rate", Math.Round(lineRate, 4).ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("branch-rate", Math.Round(branchRate, 4).ToString(CultureInfo.InvariantCulture));
    }

    private static string RelativeName(string path, string? root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return path;
        }

        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? path
            : relative.Replace('\\', '/');
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
