// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Xunit;

namespace Piston.Engine.Tests.Coverage;

public sealed class CoverageExporterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("piston-export-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CoverageFileSummary Summary(string path, string module, bool stale = false) =>
        new(path, module, 0, 0, 0, 0, stale, IsPartial: false, "hash", 1, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

    private (CoverageReportSnapshot Report, Dictionary<string, FileCoverageSnapshot> Files) Sample()
    {
        var a = Path.Combine(_root, "src", "A.cs");
        var b = Path.Combine(_root, "src", "B.cs");
        var stale = Path.Combine(_root, "src", "Stale.cs");
        var files = new Dictionary<string, FileCoverageSnapshot>
        {
            [a] = new(Summary(a, "Lib"), [new(1, 3), new(2, 0), new(4, 1, 1, 2)]),
            [b] = new(Summary(b, "Other"), [new(7, 2)]),
            [stale] = new(Summary(stale, "Lib", stale: true), [new(1, 1)]),
        };
        return (new CoverageReportSnapshot(files.Values.Select(f => f.File).ToList()), files);
    }

    [Theory]
    [InlineData("lcov", CoverageExportFormat.Lcov)]
    [InlineData("LCOV", CoverageExportFormat.Lcov)]
    [InlineData("cobertura", CoverageExportFormat.Cobertura)]
    [InlineData("xml", CoverageExportFormat.Cobertura)]
    public void TryParseFormat_AcceptsKnownNames(string value, CoverageExportFormat expected)
    {
        Assert.True(CoverageExporter.TryParseFormat(value, out var format));
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("html")]
    public void TryParseFormat_RejectsOthers(string? value) =>
        Assert.False(CoverageExporter.TryParseFormat(value, out _));

    [Fact]
    public void Lcov_WritesRecordsForFreshFiles()
    {
        var (report, files) = Sample();

        var export = CoverageExporter.Export(CoverageExportFormat.Lcov, report, p => files.GetValueOrDefault(p), _root);

        Assert.Equal(2, export.FileCount);
        Assert.Equal(1, export.StaleFilesSkipped);
        var expected = $"""
            TN:
            SF:{Path.Combine(_root, "src", "A.cs")}
            BRDA:4,0,0,1
            BRDA:4,0,1,0
            BRF:2
            BRH:1
            DA:1,3
            DA:2,0
            DA:4,1
            LF:3
            LH:2
            end_of_record
            TN:
            SF:{Path.Combine(_root, "src", "B.cs")}
            DA:7,2
            LF:1
            LH:1
            end_of_record

            """.ReplaceLineEndings("\n");
        Assert.Equal(expected, export.Content);
    }

    [Fact]
    public void Cobertura_RoundTripsThroughTheParser()
    {
        var (report, files) = Sample();

        var export = CoverageExporter.Export(CoverageExportFormat.Cobertura, report, p => files.GetValueOrDefault(p), _root);
        var path = Path.Combine(_root, "coverage.cobertura.xml");
        File.WriteAllText(path, export.Content);
        var parsed = new CoberturaParser().Parse(path);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", export.Content, StringComparison.Ordinal);
        Assert.Contains("lines-valid=\"4\"", export.Content, StringComparison.Ordinal);
        Assert.Contains("filename=\"src/A.cs\"", export.Content, StringComparison.Ordinal);
        Assert.Equal(2, parsed.Files.Count);
        var a = Assert.Single(parsed.Files, f => f.FilePath == Path.Combine(_root, "src", "A.cs"));
        Assert.Equal("Lib", a.Module);
        Assert.Equal(files[a.FilePath].Lines, a.Lines);
        Assert.DoesNotContain(parsed.Files, f => f.FilePath.EndsWith("Stale.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Export_EmptyReport_IsValid()
    {
        var export = CoverageExporter.Export(CoverageExportFormat.Cobertura, new([]), _ => null, _root);

        Assert.Equal(0, export.FileCount);
        Assert.Contains("lines-valid=\"0\"", export.Content, StringComparison.Ordinal);
    }
}
